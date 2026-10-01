using System.Collections.Concurrent;

namespace MintPlayer.Spark.Contributions;

/// <summary>
/// Everything the runtime needs to know about one <see cref="ContributionAttribute"/> declaration,
/// generated per declaration as <c>{Target}{Property}ContributionMetadata</c>. Nothing here is
/// discovered by reflection: the generator writes every id, accessor and mapping as plain code.
/// </summary>
/// <remarks>
/// Registered in <see cref="ContributionRegistry"/> by a generated module initializer, one per
/// assembly that declares contributions.
/// </remarks>
public abstract class ContributionDescriptor
{
    /// <summary>
    /// The rendering hint the attribution row attributes (<c>ContributorName</c>, <c>UpdatedAt</c>,
    /// <c>ContributionCount</c>) carry, so ng-spark's AsDetail row renderer can pick them out (PRD Q6).
    /// </summary>
    public const string AttributionRenderingHint = "contributionAttribution";

    /// <summary>
    /// The custom-query method that serves every generated <c>{Target}{Property}Contributions</c> query
    /// (source <c>Custom.SparkContributionsOfTarget</c>): the target's contributions, given the target as
    /// the query's parent. Served by the runtime's actions for the generated contribution type.
    /// </summary>
    public const string ContributionsQueryMethod = "SparkContributionsOfTarget";

    /// <summary>
    /// The <c>rendererOptions</c> written with <see cref="AttributionRenderingHint"/> on each attribution
    /// attribute of the element (contributions M5b; the client contract is in the library README):
    /// <c>contributionsQuery</c> (the generated query's alias), <c>targetType</c> (the target's model
    /// name, the query's <c>parentType</c>), <c>property</c>, <c>slots</c> (the slot attribute names, in
    /// id order — the history link's column filters) and <c>attribution</c> (the attribution attribute
    /// names the row carries).
    /// </summary>
    public static IReadOnlyDictionary<string, object> AttributionRendererOptions(ContributionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["contributionsQuery"] = global::MintPlayer.Spark.Abstractions.SparkQueryAliases.Derive(descriptor.ContributionsQueryName),
            ["targetType"] = descriptor.TargetType.Name,
            ["property"] = descriptor.PropertyName,
            ["slots"] = descriptor.SlotNames.ToArray(),
            ["attribution"] = descriptor.AttributionAttributeNames.ToArray(),
        };
    }

    /// <summary>
    /// The rendering hint seeded on the contribution type's string value attributes (<c>Text</c>):
    /// ng-spark's generic line-diff renderer, which diffs the contribution's text against the slot's
    /// current text (PRD Q7, "opening a contribution shows a line diff against the current text").
    /// </summary>
    public const string LineDiffRenderingHint = "lineDiff";

    /// <summary>
    /// The <c>rendererOptions</c> of <see cref="LineDiffRenderingHint"/> on <paramref name="valueName"/>
    /// (contributions M5b client): compare against the <b>target's</b> row of the same slot, whose value is
    /// the current version, so a reader of the history needs no right on the current type.
    /// <c>compareType</c> (the target's model name), <c>compareIdPattern</c>/<c>compareIdReplacement</c>
    /// (a regular expression and replacement deriving the target id from the contribution id),
    /// <c>compareAttribute</c> (the property), and for a collection
    /// <c>compareRowKeyPattern</c>/<c>compareRowKeyReplacement</c> (deriving the row key, the slot tuple)
    /// and <c>compareRowAttribute</c> (the value on the row).
    /// </summary>
    public static IReadOnlyDictionary<string, object> LineDiffRendererOptions(ContributionDescriptor descriptor, string valueName)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(valueName);
        // {targetId}/{Property}Contributions/{slot}/…/User/{userId} — slots are [A-Za-z0-9-], never '/'.
        var slots = descriptor.IsCollection && descriptor.SlotNames.Count > 0
            ? "(" + string.Join("/", Enumerable.Repeat("[^/]+", descriptor.SlotNames.Count)) + ")/"
            : "";
        var pattern = "^(.+?)/" + System.Text.RegularExpressions.Regex.Escape(descriptor.PropertyName) + "Contributions/" + slots + "User/.+$";
        var options = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["compareType"] = descriptor.TargetType.Name,
            ["compareIdPattern"] = pattern,
            ["compareIdReplacement"] = "$1",
            ["compareAttribute"] = descriptor.PropertyName,
            // The value on the compared row (a collection) or on the embedded object (single-valued).
            ["compareRowAttribute"] = valueName,
        };
        if (descriptor.IsCollection)
        {
            options["compareRowKeyPattern"] = pattern;
            options["compareRowKeyReplacement"] = slots.Length > 0 ? "$2" : "";
        }
        return options;
    }

    /// <summary>The entity that declares the property (<c>Song</c>).</summary>
    public abstract Type TargetType { get; }

    /// <summary>The contribution element (<c>Lyrics</c>).</summary>
    public abstract Type ElementType { get; }

    /// <summary>The generated per-user document type (<c>SongLyricsContribution</c>).</summary>
    public abstract Type ContributionType { get; }

    /// <summary>The generated current-version document type (<c>SongLyricsCurrent</c>).</summary>
    public abstract Type CurrentType { get; }

    /// <summary>The <see cref="ContributionAttribute"/> property's name (<c>Lyrics</c>), which is also the contribution name in the ids.</summary>
    public abstract string PropertyName { get; }

    /// <summary>The generated query over the contribution type (<c>SongLyricsContributions</c>, PRD Q7/Q9).</summary>
    public abstract string ContributionsQueryName { get; }

    /// <summary>Whether the property holds a collection (one row per slot) rather than a single element (no slots).</summary>
    public abstract bool IsCollection { get; }

    /// <summary>The element's <see cref="ContributionSlotAttribute"/> properties, in declaration (= id) order.</summary>
    public abstract IReadOnlyList<string> SlotNames { get; }

    /// <summary>The value (non-slot) properties copied between the element, the contribution and the current document.</summary>
    public abstract IReadOnlyList<string> ValueNames { get; }

    /// <summary>What the declaration asked to store and show about the current version's author (PRD Q6).</summary>
    public abstract ContributionAttribution Attribution { get; }

    /// <summary>
    /// The read-only row attributes the attribution emits on the element, in this order:
    /// <c>ContributorName</c>, <c>UpdatedAt</c>, <c>ContributionCount</c> — only those asked for.
    /// </summary>
    public abstract IReadOnlyList<string> AttributionAttributeNames { get; }

    /// <summary>Whether the generated contribution type is <c>ISoftDeletable</c> (SoftDelete was referenced, PRD Q4).</summary>
    public abstract bool IsSoftDeletable { get; }

    /// <summary>
    /// A hash of the generated shape: target, property, element, slots, values, attribution and soft
    /// delete. When it differs from the one recorded at the last startup, the current documents of this
    /// declaration were written in another shape and must be rebuilt (<see cref="IContributions.RebuildCurrentAsync"/>).
    /// </summary>
    public abstract string ShapeHash { get; }

    /// <summary>
    /// The prefix of every contribution of <paramref name="targetId"/>:
    /// <c>{targetId}/{Property}Contributions/</c>. Always ends in <c>/</c>, so <c>Songs/1</c> never
    /// matches <c>Songs/12</c>.
    /// </summary>
    public abstract string ContributionPrefix(string targetId);

    /// <summary>
    /// The prefix of every current document of <paramref name="targetId"/>: <c>{targetId}/{Property}/</c>.
    /// Only meaningful for a collection; a single-valued declaration has one current document,
    /// <c>{targetId}/{Property}</c>, which is point-loaded instead.
    /// </summary>
    public abstract string CurrentPrefix(string targetId);
}

/// <summary>
/// The typed half of <see cref="ContributionDescriptor"/>: accessors and mappings between the element,
/// the contribution and the current document, all generated.
/// </summary>
/// <typeparam name="TTarget">The entity that declares the property.</typeparam>
/// <typeparam name="TElement">The contribution element.</typeparam>
/// <typeparam name="TContribution">The generated per-user document type.</typeparam>
/// <typeparam name="TCurrent">The generated current-version document type.</typeparam>
public abstract class ContributionDescriptor<TTarget, TElement, TContribution, TCurrent> : ContributionDescriptor
    where TTarget : class
    where TElement : class
    where TContribution : class, IContribution
    where TCurrent : class, ICurrentContribution
{
    /// <inheritdoc />
    public sealed override Type TargetType => typeof(TTarget);

    /// <inheritdoc />
    public sealed override Type ElementType => typeof(TElement);

    /// <inheritdoc />
    public sealed override Type ContributionType => typeof(TContribution);

    /// <inheritdoc />
    public sealed override Type CurrentType => typeof(TCurrent);

    /// <summary>The rows the property holds now: empty for <see langword="null"/>, one or none for a single-valued property.</summary>
    public abstract IReadOnlyList<TElement> GetRows(TTarget target);

    /// <summary>Replaces the property's rows (a single-valued property takes the first row, or none).</summary>
    public abstract void SetRows(TTarget target, IReadOnlyList<TElement> rows);

    /// <summary>The row's slot tuple as one string (<c>ko/Kore</c>); empty without slots. Also the row's <c>[ValueKey]</c>.</summary>
    public abstract string SlotKeyOf(TElement row);

    /// <summary>The slot tuple of a contribution document.</summary>
    public abstract string SlotKeyOfContribution(TContribution contribution);

    /// <summary>The slot tuple of a current document.</summary>
    public abstract string SlotKeyOfCurrent(TCurrent current);

    /// <summary>
    /// The name of the first slot whose formatted value is not a valid id segment
    /// (<see cref="ContributionSlotFormat.IsValid"/>), or <see langword="null"/> when every slot is valid.
    /// </summary>
    public abstract string? FindInvalidSlot(TElement row);

    /// <summary><c>{targetId}/{Property}Contributions/{slots…}/User/{userId}</c>.</summary>
    public abstract string ContributionId(string targetId, TElement row, string userId);

    /// <summary><c>{targetId}/{Property}/{slots…}</c> (<c>{targetId}/{Property}</c> without slots).</summary>
    public abstract string CurrentId(string targetId, TElement row);

    /// <summary>The prefix of every user's contribution to the row's slot: <c>{targetId}/{Property}Contributions/{slots…}/User/</c>.</summary>
    public abstract string SlotContributionPrefix(string targetId, TElement row);

    /// <summary>A new contribution holding the row's slots and values, with its natural id set.</summary>
    public abstract TContribution CreateContribution(string targetId, TElement row, string contributorId, DateTime updatedAtUtc);

    /// <summary>Copies the row's value properties onto an existing contribution (slots, target and contributor are left alone).</summary>
    public abstract void CopyValues(TElement row, TContribution contribution);

    /// <summary>
    /// The current document for <paramref name="contribution"/>, with its natural id set.
    /// <paramref name="contributionCount"/> is stored only when the declaration asks for
    /// <see cref="ContributionAttribution.History"/>.
    /// </summary>
    public abstract TCurrent CreateCurrent(TContribution contribution, int contributionCount);

    /// <summary>
    /// The row a current document shows on the target, with the attribution the declaration asks for.
    /// <paramref name="contributorName"/> is resolved by the caller at read time, never stored.
    /// </summary>
    public abstract TElement CreateRow(TCurrent current, string? contributorName);
}

/// <summary>
/// Every <see cref="ContributionDescriptor"/> of every loaded assembly, filled by generated module
/// initializers (the same scheme as <c>SparkValueObjects</c>). <c>AddContributions()</c> reads it; it
/// scans nothing.
/// </summary>
/// <remarks>
/// ⚠️ A module initializer runs when its assembly is loaded, and .NET loads an assembly on first use.
/// The runtime therefore has to make sure the assemblies that declare contributions are loaded before
/// it reads <see cref="Descriptors"/> (the entity types of the model are, by the time Spark's model is
/// loaded).
/// </remarks>
public static class ContributionRegistry
{
    private static readonly ConcurrentDictionary<(Type Target, string Property), ContributionDescriptor> Registered = new();

    /// <summary>Registers one declaration. Called by generated code; idempotent.</summary>
    public static void Register(ContributionDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Registered[(descriptor.TargetType, descriptor.PropertyName)] = descriptor;
        RegisterModel(descriptor);
    }

    /// <summary>
    /// Makes the generated types part of the model without app hand-work (contributions M5b): both
    /// are satellites of the target, so synchronization writes their model files, mints the
    /// contributions query once, and seeds the attribution renderer on the element's attribution
    /// attributes. Keyed by the target type, so only a context that exposes it sees them.
    /// </summary>
    private static void RegisterModel(ContributionDescriptor descriptor)
    {
        global::MintPlayer.Spark.Abstractions.Model.SparkModelSatellites.Register(new(
            descriptor.TargetType,
            descriptor.ContributionType,
            new(descriptor.ContributionsQueryName, "Custom." + ContributionDescriptor.ContributionsQueryMethod,
                nameof(IContribution.UpdatedAt), "desc")));
        global::MintPlayer.Spark.Abstractions.Model.SparkModelSatellites.Register(new(descriptor.TargetType, descriptor.CurrentType));

        // The raw contributor id stays off the history grid (and is not drawn on the page): the
        // resolved ContributorName is shown instead. Only for a newly created attribute.
        global::MintPlayer.Spark.Abstractions.Model.SparkModelSatellites.SeedNewAttribute(new(
            descriptor.ContributionType, nameof(IContribution.ContributorId),
            ShowedOn: global::MintPlayer.Spark.Abstractions.EShowedOn.PersistentObject, IsVisible: false));

        // Opening a contribution shows its text diffed against the current one (PRD Q7).
        foreach (var value in descriptor.ValueNames)
        {
            if (descriptor.ContributionType.GetProperty(value)?.PropertyType != typeof(string))
                continue;
            global::MintPlayer.Spark.Abstractions.Model.SparkModelSatellites.SeedRenderer(new(
                descriptor.ContributionType, value, ContributionDescriptor.LineDiffRenderingHint,
                ContributionDescriptor.LineDiffRendererOptions(descriptor, value)));
        }

        if (descriptor.AttributionAttributeNames.Count == 0)
            return;
        var options = ContributionDescriptor.AttributionRendererOptions(descriptor);
        foreach (var name in descriptor.AttributionAttributeNames)
            global::MintPlayer.Spark.Abstractions.Model.SparkModelSatellites.SeedRenderer(new(
                descriptor.ElementType, name, ContributionDescriptor.AttributionRenderingHint, options));
    }

    /// <summary>Every registered declaration.</summary>
    public static IReadOnlyCollection<ContributionDescriptor> Descriptors => (IReadOnlyCollection<ContributionDescriptor>)Registered.Values;

    /// <summary>The declaration for <paramref name="targetType"/>.<paramref name="propertyName"/>, or <see langword="null"/>.</summary>
    public static ContributionDescriptor? Find(Type targetType, string propertyName)
    {
        ArgumentNullException.ThrowIfNull(targetType);
        ArgumentNullException.ThrowIfNull(propertyName);
        return Registered.TryGetValue((targetType, propertyName), out var descriptor) ? descriptor : null;
    }

    /// <summary>Every declaration on <paramref name="targetType"/>.</summary>
    public static IEnumerable<ContributionDescriptor> ForTarget(Type targetType)
        => Registered.Values.Where(d => d.TargetType == targetType);
}
