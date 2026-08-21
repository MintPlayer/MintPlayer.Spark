using Microsoft.AspNetCore.Http;
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
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    /// <summary>
    /// Combined action patterns that include multiple individual actions.
    /// </summary>
    /// <remarks>
    /// Expansion is <b>grant-only</b>: step 3 below filters to <c>!r.IsDenied</c>. So a denial
    /// written with a combined action denies the literal string and nothing else — which is to say
    /// nothing. Symmetric syntax, asymmetric semantics; the loader refuses that shape rather than
    /// leaving it to be discovered.
    /// </remarks>
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

    /// <summary>The combined action names the loader validates against, so the set that is judged is
    /// provably the set that is expanded.</summary>
    internal static IReadOnlyCollection<string> CombinedActionNames => CombinedActions.Keys;

    /// <summary>What a combined action expands to, for a diagnostic that can name the replacement.</summary>
    internal static IReadOnlyList<string> ExpandCombinedAction(string action)
        => CombinedActions.TryGetValue(action, out var actions) ? actions : [];

    /// <summary>
    /// The group id playing <paramref name="role"/>, or null when the application declares none.
    /// <para>
    /// Read from the <c>wellKnown</c> map rather than matched against a display name. The old
    /// name-matching depended on <c>TranslatedString.GetDefaultValue()</c>, which returns the first
    /// translation in <em>file order</em> — so reordering two JSON keys silently changed
    /// authorization, and renaming a group for the UI silently un-declared its role.
    /// </para>
    /// </summary>
    private static Guid? ResolveWellKnownGroupId(SecurityConfiguration config, string role)
    {
        if (config.WellKnown is null)
            return null;

        foreach (var (key, value) in config.WellKnown)
        {
            if (string.Equals(key, role, StringComparison.OrdinalIgnoreCase) && Guid.TryParse(value, out var id))
                return id;
        }

        return null;
    }

    /// <summary>
    /// Every id declared in <c>wellKnown</c>. These are decided here, from authentication state, and
    /// are therefore excluded from claim-derived membership — otherwise a principal carrying
    /// <c>group: "Signed-in users"</c> would resolve the authenticated group's id by name and never
    /// reach the <c>IsAuthenticated</c> test at all.
    /// <para>
    /// A comment shipped in #304 asserted this guarantee already held. It did not: membership
    /// resolution matched a provider-returned name against <em>any</em> translation of <em>any</em>
    /// group, well-known ones included. Inert with the shipped claims provider, which returns nothing
    /// for an unauthenticated caller — and silently broken by any custom one.
    /// </para>
    /// </summary>
    private static HashSet<Guid> ReservedGroupIds(SecurityConfiguration config)
    {
        var reserved = new HashSet<Guid>();

        foreach (var value in config.WellKnown?.Values ?? Enumerable.Empty<string>())
        {
            if (Guid.TryParse(value, out var id))
                reserved.Add(id);
        }

        return reserved;
    }

    public async Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
    {
        var config = configLoader.GetConfiguration();
        var groupNames = await groupMembershipProvider.GetCurrentUserGroupsAsync(cancellationToken);
        var groupNamesList = groupNames.ToList();

        // Resolve group names to IDs, minus anything a well-known role claims.
        var reserved = ReservedGroupIds(config);
        var groupIds = ResolveGroupIds(config, groupNamesList, reserved);

        // The well-known roles are decided from authentication state, never from a claim.
        //
        // Note that "anonymous" is NOT the old "Everyone": it applies only while the caller has not
        // signed in. A right that both an anonymous visitor and a signed-in user should have is two
        // grants now — verbose on purpose, because the alternative is one token that quietly means
        // "the public internet".
        var isAuthenticated = httpContextAccessor?.HttpContext?.User.Identity?.IsAuthenticated == true;
        var role = isAuthenticated
            ? SecurityConfigurationValidator.Authenticated
            : SecurityConfigurationValidator.Anonymous;

        if (ResolveWellKnownGroupId(config, role) is { } wellKnownGroupId)
        {
            groupIds.Add(wellKnownGroupId);
        }

        // No groups (not even Everyone) = no access (unless default allows)
        if (groupIds.Count == 0)
        {
            var defaultResult = options.Value.DefaultBehavior == DefaultAccessBehavior.AllowAll;
            LogAuthorizationDecision(resource, groupNamesList, defaultResult, "no groups (default behavior)");
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

    private static HashSet<Guid> ResolveGroupIds(
        SecurityConfiguration config, IEnumerable<string> groupNames, HashSet<Guid> reserved)
    {
        var result = new HashSet<Guid>();

        foreach (var groupName in groupNames)
        {
            // Find group by name (case-insensitive, matches against any translation)
            var matchingGroup = config.Groups
                .FirstOrDefault(g => g.Value.Translations.Values
                    .Any(v => string.Equals(v, groupName, StringComparison.OrdinalIgnoreCase)));

            if (string.IsNullOrEmpty(matchingGroup.Key) || !Guid.TryParse(matchingGroup.Key, out var groupId))
                continue;

            // A well-known role is not assertable by claim, whatever an IGroupMembershipProvider
            // returns. Dropped silently rather than refused: an external identity provider naming a
            // group Spark reserves is not the caller's doing, and failing the request would turn
            // someone else's naming choice into an outage.
            if (reserved.Contains(groupId))
                continue;

            result.Add(groupId);
        }

        return result;
    }

    private bool MatchesResource(string rightResource, string requestedResource)
    {
        return string.Equals(rightResource, requestedResource, StringComparison.OrdinalIgnoreCase);
    }

    private (string Action, string Target) ParseResource(string resource)
    {
        var slashIndex = resource.IndexOf('/');
        if (slashIndex < 0)
        {
            return (resource, string.Empty);
        }

        return (resource[..slashIndex], resource[(slashIndex + 1)..]);
    }

    private bool IsCombinedActionMatch(string rightResource, string requestedAction, string requestedTarget)
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
