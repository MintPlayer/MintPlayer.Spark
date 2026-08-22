namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// Loads and caches <c>App_Data/security.json</c>.
/// </summary>
public interface ISecurityConfigurationLoader
{
    /// <summary>
    /// The current security configuration, from cache when one is warm.
    /// <para>
    /// Never returns an empty configuration to paper over a missing or unreadable file — that is a
    /// startup failure, not a runtime one, and a loader that silently returns "no rights" turns a
    /// deployment mistake into an application that denies everything for no visible reason.
    /// </para>
    /// </summary>
    SecurityConfiguration GetConfiguration();

    /// <summary>
    /// What <paramref name="groupIds"/> may do, with combined actions already expanded.
    /// <para>
    /// The expansion is derived once per group per loaded file and memoised, so evaluating a right
    /// is a handful of set probes rather than a scan of the rights list. It lives on the loader
    /// because it is a pure function of the file — leaving it to the evaluator meant rebuilding it
    /// per request, and per request is exactly where the asymmetry between expanding grants and
    /// not expanding denials crept in.
    /// </para>
    /// </summary>
    RightsDecision GetResolvedRights(IReadOnlySet<Guid> groupIds);

    /// <summary>
    /// Invalidates the cached configuration, forcing a reload on next access.
    /// </summary>
    void InvalidateCache();
}

/// <summary>
/// The rights of every group a caller belongs to, ready to answer <see cref="Allows"/>.
/// </summary>
/// <remarks>
/// Holds the per-group sets by reference rather than merging them, so asking a question costs no
/// allocation and the memoised sets are shared across every request that resolves the same group.
/// </remarks>
public sealed class RightsDecision(IReadOnlyList<GroupRights> groups)
{
    /// <summary>A caller in no group: every probe refuses.</summary>
    public static readonly RightsDecision None = new([]);

    /// <summary>
    /// Whether <paramref name="resource"/> — <c>{action}/{target}</c> — is allowed.
    /// <para>
    /// Four tiers, in this order, each evaluated across <em>all</em> of the caller's groups before
    /// the next is considered:
    /// </para>
    /// <list type="number">
    /// <item>an important denial refuses, whatever else is granted;</item>
    /// <item>an important grant allows, over any ordinary denial;</item>
    /// <item>an ordinary denial refuses;</item>
    /// <item>an ordinary grant allows.</item>
    /// </list>
    /// <para>
    /// Anything else is refused. The whole-set-per-tier order is the point: it makes a denial
    /// absolute rather than something another group's grant can outrun, and it is what stops
    /// <c>grant Read/Car</c> plus <c>deny QueryReadEditNewDelete/Car</c> from resolving to
    /// <em>allowed</em> — which is what a per-right chain does, because the exact grant fires
    /// before the expanded denial is ever looked at.
    /// </para>
    /// </summary>
    public bool Allows(string resource)
    {
        var probe = ResourcePattern.Parse(resource);

        if (AnyMatches(static g => g.ImportantDenied, probe)) return false;
        if (AnyMatches(static g => g.ImportantAllowed, probe)) return true;
        if (AnyMatches(static g => g.Denied, probe)) return false;
        return AnyMatches(static g => g.Allowed, probe);
    }

    private bool AnyMatches(Func<GroupRights, IReadOnlySet<ResourcePattern>> tier, ResourcePattern probe)
    {
        foreach (var group in groups)
        {
            if (Covers(tier(group), probe))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether any pattern in <paramref name="patterns"/> covers <paramref name="probe"/>.
    /// <para>
    /// Four lookups rather than a scan: a concrete resource is covered by at most its own pattern
    /// and the three wildcard forms, so the tier stays a hash set even though the patterns in it
    /// are not all concrete.
    /// </para>
    /// </summary>
    private static bool Covers(IReadOnlySet<ResourcePattern> patterns, ResourcePattern probe)
        => patterns.Count != 0
        && (patterns.Contains(probe)
            || patterns.Contains(probe with { Action = ResourcePattern.Wildcard })
            || patterns.Contains(probe with { Target = ResourcePattern.Wildcard })
            || patterns.Contains(ResourcePattern.Any));
}

/// <summary>
/// One group's rights, expanded. Four tiers so that <see cref="Right.IsImportant"/> and
/// <see cref="Right.IsDenied"/> are decided by which set a pattern landed in, never by re-reading
/// the flags at evaluation time.
/// </summary>
public sealed record GroupRights(
    IReadOnlySet<ResourcePattern> ImportantDenied,
    IReadOnlySet<ResourcePattern> ImportantAllowed,
    IReadOnlySet<ResourcePattern> Denied,
    IReadOnlySet<ResourcePattern> Allowed);

/// <summary>
/// A parsed <c>{action}/{target}</c> resource, with <c>*</c> allowed on either half.
/// </summary>
/// <remarks>
/// Case-insensitive by construction — both halves are upper-cased on parse — so the record's
/// generated equality and hash code <em>are</em> the comparison, and no call site can accidentally
/// fall back to an ordinal one.
/// </remarks>
public readonly record struct ResourcePattern(string Action, string Target)
{
    /// <summary>Matches any value in the half it appears in.</summary>
    public const string Wildcard = "*";

    /// <summary>Matches every resource.</summary>
    public static readonly ResourcePattern Any = new(Wildcard, Wildcard);

    /// <summary>
    /// Parses <c>{action}/{target}</c>. A string with no slash becomes the action with an empty
    /// target, which is what the old exact-equality matcher effectively did with one.
    /// </summary>
    public static ResourcePattern Parse(string resource)
    {
        var slash = resource.IndexOf('/');
        return slash < 0
            ? new ResourcePattern(Normalize(resource), string.Empty)
            : new ResourcePattern(Normalize(resource[..slash]), Normalize(resource[(slash + 1)..]));
    }

    private static string Normalize(string value) => value.ToUpperInvariant();

    public override string ToString() => $"{Action}/{Target}";
}
