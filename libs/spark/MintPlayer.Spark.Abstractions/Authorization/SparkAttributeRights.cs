namespace MintPlayer.Spark.Abstractions.Authorization;

/// <summary>
/// The syntax of attribute-level rights: <c>{verb}/{Type}/{Attribute}</c>, for example
/// <c>Edit/Song/Lyrics</c> or <c>QueryRead/Employee/Salary</c> (#460 contributions, PRD §5 Q12).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only four verbs have an attribute form</b> — <see cref="SparkCoreActions.Query"/>,
/// <see cref="SparkCoreActions.Read"/>, <see cref="SparkCoreActions.Edit"/> and
/// <see cref="SparkCoreActions.New"/> — plus the combined verbs made solely of them, which expand as
/// prefixes exactly as they do at type level (<c>QueryRead/T/A</c> is <c>Query/T/A</c> +
/// <c>Read/T/A</c>). Delete, <c>Replicate</c>, the package verbs and every custom action stay
/// type-level, and a third segment on any of them refuses the file at startup.
/// </para>
/// <para>
/// <b>The type right is required</b> (Q13): an attribute right composes over its type right and never
/// unlocks what the type right withholds. See <see cref="EffectiveAttributeRights"/>.
/// </para>
/// <para>
/// An AsDetail row's attributes target the <em>row type</em> (<c>Edit/Lyrics/Text</c>), so an
/// attribute name never contains a slash or a dotted path.
/// </para>
/// </remarks>
public static class SparkAttributeRights
{
    /// <summary>The verbs that have an attribute-level form.</summary>
    public static IReadOnlyList<string> Verbs { get; } =
        [SparkCoreActions.Query, SparkCoreActions.Read, SparkCoreActions.Edit, SparkCoreActions.New];

    /// <summary>
    /// Whether <paramref name="action"/> may carry an attribute segment: one of <see cref="Verbs"/>,
    /// or a combined verb whose every expansion is one of them (so <c>EditNew</c> is, and
    /// <c>EditNewDelete</c> is not).
    /// </summary>
    public static bool IsAttributeAction(string action)
    {
        if (string.IsNullOrEmpty(action))
            return false;

        foreach (var expanded in SparkCombinedActions.Expand(action))
        {
            if (!IsVerb(expanded))
                return false;
        }

        return true;
    }

    /// <summary>Whether <paramref name="verb"/> is exactly one of <see cref="Verbs"/> (case-insensitive).</summary>
    public static bool IsVerb(string verb)
    {
        foreach (var candidate in Verbs)
        {
            if (string.Equals(candidate, verb, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Every combined verb that may carry an attribute segment, for messages.
    /// </summary>
    public static IEnumerable<string> CombinedAttributeActions
        => SparkCombinedActions.Names.Where(IsAttributeAction).OrderBy(n => n, StringComparer.Ordinal);

    /// <summary>
    /// Splits a resource with at least two slashes into its action, type and attribute, keeping the
    /// written casing (for messages). Returns false for a type-level <c>{action}/{target}</c>
    /// resource. The attribute is everything after the second slash, so a fourth segment surfaces as
    /// an attribute containing '/', which the validator refuses.
    /// </summary>
    public static bool TrySplit(string? resource, out string action, out string type, out string attribute)
    {
        action = type = attribute = string.Empty;
        if (string.IsNullOrEmpty(resource))
            return false;

        var first = resource.IndexOf('/');
        if (first < 0)
            return false;

        var second = resource.IndexOf('/', first + 1);
        if (second < 0)
            return false;

        action = resource[..first];
        type = resource[(first + 1)..second];
        attribute = resource[(second + 1)..];
        return true;
    }

    /// <summary>The resource string for one attribute: <c>{verb}/{type}/{attribute}</c>.</summary>
    public static string Resource(string verb, string type, string attribute) => $"{verb}/{type}/{attribute}";
}

/// <summary>
/// What a caller may do with each attribute of one entity type, for one verb — the effective
/// (type, attribute, verb) table of PRD §5 Q13, computed once per request per (type, verb) and never
/// per row.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TypeAllowed"/> is the type-level decision, exactly as <c>IsAllowedAsync("{verb}/{type}")</c>
/// answers it. It is <b>required</b>: when it is false, every attribute is refused, whatever an
/// attribute-level grant says (no unlocking, unlike Vidyano).
/// </para>
/// <para>
/// <see cref="Attributes"/> holds only the attributes some attribute-level right of the caller's
/// groups mentions for this verb (combined verbs and the Read ⇒ Query grant implication included),
/// already composed with the type decision. Every other attribute inherits <see cref="TypeAllowed"/>
/// — ask through <see cref="IsAllowed"/> rather than reading the dictionary, so the inherit rule is
/// never re-implemented at a call site.
/// </para>
/// </remarks>
public sealed class EffectiveAttributeRights
{
    private static readonly IReadOnlyDictionary<string, bool> NoAttributes =
        new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    public EffectiveAttributeRights(
        string entityType, string verb, bool typeAllowed, IReadOnlyDictionary<string, bool>? attributes = null,
        bool isUnrestricted = false)
    {
        EntityType = entityType;
        Verb = verb;
        TypeAllowed = typeAllowed;
        IsUnrestricted = isUnrestricted;

        if (attributes is null || attributes.Count == 0)
        {
            Attributes = NoAttributes;
        }
        else
        {
            // Normalised: case-insensitive, and composed with the type decision so no entry can
            // claim an allow the type right withholds.
            var normalized = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, allowed) in attributes)
                normalized[name] = typeAllowed && allowed;
            Attributes = normalized;
        }
    }

    /// <summary>A decision with no attribute-level overrides: every attribute inherits <paramref name="typeAllowed"/>.</summary>
    public static EffectiveAttributeRights Inherit(string entityType, string verb, bool typeAllowed)
        => new(entityType, verb, typeAllowed);

    /// <summary>System context (sync, replication): everything allowed, attribute rights bypassed.</summary>
    public static EffectiveAttributeRights Unrestricted(string entityType, string verb)
        => new(entityType, verb, typeAllowed: true, isUnrestricted: true);

    /// <summary>The entity type's name, as the resource names it.</summary>
    public string EntityType { get; }

    /// <summary>One of <see cref="SparkAttributeRights.Verbs"/>.</summary>
    public string Verb { get; }

    /// <summary>The type-level decision. Required: false refuses every attribute.</summary>
    public bool TypeAllowed { get; }

    /// <summary>True in system context, where attribute rights do not apply.</summary>
    public bool IsUnrestricted { get; }

    /// <summary>
    /// The attributes an attribute-level right mentions for this verb, with their composed decision.
    /// Case-insensitive. Attributes absent here inherit <see cref="TypeAllowed"/>.
    /// </summary>
    public IReadOnlyDictionary<string, bool> Attributes { get; }

    /// <summary>Whether the caller may exercise <see cref="Verb"/> on <paramref name="attributeName"/>.</summary>
    public bool IsAllowed(string attributeName)
        => TypeAllowed && (!Attributes.TryGetValue(attributeName, out var allowed) || allowed);

    /// <summary>
    /// The attributes refused while the type itself is allowed. When <see cref="TypeAllowed"/> is
    /// false every attribute is refused and this lists only the explicitly mentioned ones — check
    /// <see cref="TypeAllowed"/> first.
    /// </summary>
    public IEnumerable<string> DeniedAttributes
        => Attributes.Where(a => !a.Value).Select(a => a.Key);
}
