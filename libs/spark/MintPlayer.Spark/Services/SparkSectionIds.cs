using System.Text;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The section ids of an object page (#721, <c>docs/section_fragment_links_PRD.md</c>): the DOM id of each group card, tab
/// and sub-query card, which a URL's <c>#fragment</c> names.
/// </summary>
/// <remarks>
/// <para>
/// <b>Computed once, here (Dec1).</b> A group's or tab's id is its <c>name</c> in kebab case, served as <c>anchor</c> on the
/// HTTP JSON only (<see cref="AddAnchorProperty"/>), so the synchronizer, which writes with its own options, never puts it in
/// a model file. A sub-query's id is its query's alias; the catalogue serves an entry that names its query by id in alias
/// form (<see cref="SubQueryPruner"/>). The client renders what it receives and has no kebab function of its own.
/// </para>
/// <para>
/// <b>Refused, never stripped (Dec2).</b> The name is not a label: it is an identifier, and translation keys use it. One that
/// does not give <c>^[a-z][a-z0-9]*(-[a-z0-9]+)*$</c>, or gives an id starting with <c>spark-</c> (the shell's) or <c>bs-</c>
/// (the tab control's), is a model problem.
/// </para>
/// <para>
/// <b>Checked in three places (Dec7):</b> <c>--spark-verify-model</c>, startup and the synchronizer, like
/// <see cref="SparkSubQueries.ValidateParentReferences"/>.
/// </para>
/// </remarks>
public static partial class SparkSectionIds
{
    [GeneratedRegex("^[a-z][a-z0-9]*(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    private static readonly string[] ReservedPrefixes = ["spark-", "bs-"];

    /// <summary>
    /// The section id of a group or tab named <paramref name="name"/>: kebab case, acronym-aware (<c>PrSizeLabels</c> →
    /// <c>pr-size-labels</c>, <c>URLPath</c> → <c>url-path</c>, <c>Step2Details</c> → <c>step2-details</c>). Null when the
    /// result is no valid id or starts with a reserved prefix.
    /// </summary>
    public static string? Anchor(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return null;

        var id = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsAsciiLetterUpper(c))
            {
                // A word starts at an upper-case letter after a lower-case letter or a digit, and at the last letter of an
                // upper-case run that a lower-case letter follows (URLPath: the P).
                var previous = name[i - 1];
                var next = i + 1 < name.Length ? name[i + 1] : '\0';
                if (char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous)
                    || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next)))
                    id.Append('-');
            }
            id.Append(char.ToLowerInvariant(c));
        }

        return IsValidId(id.ToString()) ? id.ToString() : null;
    }

    /// <summary>
    /// The section id of a sub-query entry: its query's alias, declared or derived, whether the entry names the query by
    /// alias or by id. Null when the entry resolves to no query, which <see cref="SubQueryPruner"/> warns about.
    /// </summary>
    public static string? SubQueryAnchor(SparkSubQuery entry, IEnumerable<SparkQuery> queries)
        => Resolve(entry.Query, queries) is { } query ? AliasOf(query) : null;

    /// <summary>
    /// Every problem with the section ids of <paramref name="entityTypes"/>, one message per offender: a group, tab or
    /// sub-query that gives no valid id, and two sections of one type that give the same id.
    /// </summary>
    public static IReadOnlyList<string> Validate(IEnumerable<EntityTypeDefinition> entityTypes, IEnumerable<SparkQuery> queries)
    {
        var queryList = queries.ToList();
        var problems = new List<string>();

        foreach (var type in entityTypes)
        {
            var taken = new Dictionary<string, string>(StringComparer.Ordinal);

            void Add(string section, string? id)
            {
                if (id is null)
                {
                    problems.Add(
                        $"'{type.Name}': the {section} gives no valid section id. A group's or tab's id is its name in kebab case " +
                        $"(PrSizeLabels → pr-size-labels) and a sub-query's is its query's alias; the id must match " +
                        $"^[a-z][a-z0-9]*(-[a-z0-9]+)*$ and must not start with 'spark-' or 'bs-'. Rename it.");
                }
                else if (taken.TryGetValue(id, out var other))
                {
                    problems.Add(
                        $"'{type.Name}': the {other} and the {section} both give the section id '{id}'. Section ids are unique " +
                        $"within a persistent object, because a link's #fragment names one; rename one of them.");
                }
                else
                {
                    taken[id] = section;
                }
            }

            foreach (var group in type.Groups)
                Add($"group '{group.Name}'", Anchor(group.Name));
            foreach (var tab in type.Tabs)
                Add($"tab '{tab.Name}'", Anchor(tab.Name));
            foreach (var entry in type.Queries)
            {
                // An unresolvable entry is SubQueryPruner's warning, not this one: its card would fail to load anyway.
                if (Resolve(entry.Query, queryList) is not { } query)
                    continue;
                var alias = AliasOf(query);
                Add($"sub-query '{alias}'", IsValidId(alias) ? alias : null);
            }
        }

        return problems;
    }

    /// <summary>Startup's check: throws naming every problem <see cref="Validate"/> finds.</summary>
    internal static void ThrowIfInvalid(IEnumerable<EntityTypeDefinition> entityTypes, IEnumerable<SparkQuery> queries)
    {
        var problems = Validate(entityTypes, queries);
        if (problems.Count > 0)
            throw new InvalidOperationException("Invalid section ids (#721): " + string.Join(" ", problems));
    }

    /// <summary>
    /// Adds the served-only <c>anchor</c> property to the <see cref="AttributeGroup"/> and <see cref="AttributeTab"/>
    /// contracts of the HTTP JSON options (Dec1). The synchronizer's own options never get it, so no model file does.
    /// </summary>
    internal static void AddAnchorProperty(JsonTypeInfo typeInfo)
    {
        Func<object, string?>? name = typeInfo.Type == typeof(AttributeGroup) ? value => ((AttributeGroup)value).Name
            : typeInfo.Type == typeof(AttributeTab) ? value => ((AttributeTab)value).Name
            : null;
        if (name is null || typeInfo.Kind != JsonTypeInfoKind.Object || typeInfo.Properties.Any(p => p.Name == "anchor"))
            return;

        var anchor = typeInfo.CreateJsonPropertyInfo(typeof(string), "anchor");
        anchor.Get = value => Anchor(name(value));
        anchor.ShouldSerialize = (_, value) => value is not null;
        typeInfo.Properties.Add(anchor);
    }

    internal static string AliasOf(SparkQuery query) => query.Alias ?? SparkQueryAliases.Derive(query.Name);

    /// <summary>The query an entry names, as <c>QueryLoader.ResolveQuery</c> finds it: by id, else by alias, ignoring case.</summary>
    internal static SparkQuery? Resolve(string? idOrAlias, IEnumerable<SparkQuery> queries)
    {
        if (string.IsNullOrEmpty(idOrAlias))
            return null;
        if (Guid.TryParse(idOrAlias, out var id))
            return queries.FirstOrDefault(q => q.Id == id);
        return queries.FirstOrDefault(q => string.Equals(AliasOf(q), idOrAlias, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsValidId(string id)
        => IdPattern().IsMatch(id) && !ReservedPrefixes.Any(prefix => id.StartsWith(prefix, StringComparison.Ordinal));
}
