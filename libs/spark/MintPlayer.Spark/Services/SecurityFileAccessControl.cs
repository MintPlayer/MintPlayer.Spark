using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Decides every Spark authorization question from <c>App_Data/security.json</c>.
/// </summary>
/// <remarks>
/// <b>Scoped, not singleton.</b> It reads authentication state from
/// <see cref="IHttpContextAccessor"/> to choose between the <c>anonymous</c> and
/// <c>authenticated</c> roles; a singleton would still work by accident today but would be one
/// cached field away from answering one caller's question with another caller's identity.
/// <para>
/// The evaluation itself is a probe into the loader's expanded index — see
/// <see cref="RightsDecision.Allows"/> for the four tiers and why they are ordered that way. This
/// class is only responsible for working out <em>which groups the caller is in</em>.
/// </para>
/// </remarks>
[Register(typeof(IAccessControl), ServiceLifetime.Scoped)]
internal partial class SecurityFileAccessControl : IAccessControl
{
    [Inject] private readonly ISecurityConfigurationLoader configLoader;
    [Inject] private readonly SparkGroupMembership groupMembership;
    [Inject] private readonly ILogger<SecurityFileAccessControl> logger;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    private (SecurityConfiguration Config, SparkRequestGroups Groups, HashSet<Guid> Ids)? resolved;

    public async Task<bool> IsAllowedAsync(string resource, CancellationToken cancellationToken = default)
    {
        var (decision, groups) = await ResolveDecisionAsync(cancellationToken);
        var allowed = decision.Allows(resource);

        LogAuthorizationDecision(resource, groups.Names, allowed);
        return allowed;
    }

    /// <summary>
    /// The attribute-level composition over the same groups <see cref="IsAllowedAsync"/> resolves —
    /// see <see cref="RightsDecision.ForAttributes"/>. Not memoised here; <c>IAttributeRights</c> is.
    /// </summary>
    public async Task<EffectiveAttributeRights> GetAttributeRightsAsync(
        string verb, string entityTypeName, CancellationToken cancellationToken = default)
    {
        var (decision, groups) = await ResolveDecisionAsync(cancellationToken);
        var rights = decision.ForAttributes(verb, entityTypeName);

        LogAuthorizationDecision($"{verb}/{entityTypeName}", groups.Names, rights.TypeAllowed);
        return rights;
    }

    private async Task<(RightsDecision Decision, SparkRequestGroups Groups)> ResolveDecisionAsync(CancellationToken cancellationToken)
    {
        var config = configLoader.GetConfiguration();
        var groups = await groupMembership.GetAsync(cancellationToken);

        // Resolve claim-asserted group names (and provider-asserted ids) to ids, minus anything a
        // well-known role claims. Memoised per request and configuration — see ResolvedGroupIds.
        var groupIds = new HashSet<Guid>(ResolvedGroupIds(config, groups));

        // The well-known roles are decided from authentication state, never from a claim.
        //
        // Note that "anonymous" is NOT the old "Everyone": it applies only while the caller has not
        // signed in. A right that both an anonymous visitor and a signed-in user should have is two
        // grants now — verbose on purpose, because the alternative is one token that quietly means
        // "the public internet".
        var isAuthenticated = httpContextAccessor?.HttpContext?.User.Identity?.IsAuthenticated == true;
        var role = isAuthenticated ? SparkWellKnownGroups.Authenticated : SparkWellKnownGroups.Anonymous;

        if (ResolveWellKnownGroupId(config, role) is { } wellKnownGroupId)
            groupIds.Add(wellKnownGroupId);

        return (configLoader.GetResolvedRights(groupIds), groups);
    }

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

    /// <summary>
    /// The caller's non-reserved group ids under <paramref name="config"/>, resolved once per
    /// request. Keyed on both inputs by reference, so a hot-reloaded file or a replaced principal
    /// (which yields a fresh <see cref="SparkRequestGroups"/>) is resolved again rather than
    /// answered from the old snapshot.
    /// </summary>
    private IReadOnlySet<Guid> ResolvedGroupIds(SecurityConfiguration config, SparkRequestGroups groups)
    {
        if (resolved is { } hit && ReferenceEquals(hit.Config, config) && ReferenceEquals(hit.Groups, groups))
            return hit.Ids;

        var reserved = ReservedGroupIds(config);
        var ids = ResolveGroupIds(config, groups.Names, reserved);

        // Ids a provider asserts directly (IGroupIdMembershipProvider) obey the same two rules as a
        // resolved name: a well-known id is never assertable, and an undeclared one names nothing.
        foreach (var id in groups.Ids)
        {
            if (!reserved.Contains(id) && IsDeclared(config, id))
                ids.Add(id);
        }

        resolved = (config, groups, ids);
        return ids;
    }

    private static bool IsDeclared(SecurityConfiguration config, Guid id)
        => config.Groups.Keys.Any(k => Guid.TryParse(k, out var declared) && declared == id);

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

    private void LogAuthorizationDecision(string resource, IEnumerable<string> groups, bool allowed)
    {
        var groupsString = string.Join(", ", groups);

        if (allowed)
            logger.LogDebug("Authorization ALLOWED for {Resource} (groups: [{Groups}])", resource, groupsString);
        else
            logger.LogWarning("Authorization DENIED for {Resource} (groups: [{Groups}])", resource, groupsString);
    }
}
