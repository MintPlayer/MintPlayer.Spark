using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Applies the caller's static attribute rights (PRD §5 Q14) to what the read paths put on the wire:
/// an attribute the caller may not <c>Read</c> is <b>absent</b> from a persistent object and from the
/// caller's entity-type definition, one they may not <c>Query</c> is absent from the query surface,
/// and one they may not <c>Edit</c> (or <c>New</c>) is read-only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Absent, not blanked.</b> A static right does not vary per row, so removing the attribute
/// discloses nothing a row could not: every row of the type is missing the same attribute for the
/// same caller. That is the difference from the per-row <c>GetProtectedAttributesAsync</c> hook,
/// which keeps the attribute and empties its value, because a per-row absence would itself be the
/// signal.
/// </para>
/// <para>
/// One decision per (type, verb) per request — <see cref="IAttributeRights"/> memoises it, and the
/// denied sets are memoised here — then applied to every row. Nothing here asks per row.
/// </para>
/// <para>
/// Only attributes an attribute-level right <em>mentions</em> are ever removed. The type-level right
/// was decided before any of this runs (a caller without <c>Read/T</c> never reaches a presentation
/// of a <c>T</c>), and an AsDetail row type usually holds no type right of its own — the parent's
/// governs — so treating "type not granted" as "every attribute denied" would empty every embedded
/// row.
/// </para>
/// </remarks>
internal interface IAttributeRightsEnforcement
{
    /// <summary>
    /// The attributes of <paramref name="definition"/> the caller is refused for
    /// <paramref name="verb"/>, in the model's spelling, compared case-insensitively. Empty in
    /// system context and when no attribute right mentions the type.
    /// </summary>
    Task<IReadOnlySet<string>> GetDeniedAsync(EntityTypeDefinition definition, string verb, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's query surface: <paramref name="definition"/> without its <c>Query</c>-denied
    /// attributes, so columns, search fields, sort, filter and distincts treat them as if they did
    /// not exist. The same reference when nothing is denied; otherwise a copy — never a mutation of
    /// the loader's shared definition.
    /// </summary>
    Task<EntityTypeDefinition> ForQueryAsync(EntityTypeDefinition definition, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's form definition (<c>EntityTypes/Get|List</c>) for <paramref name="verb"/>, on the type
    /// and on every embedded detail type by that type's own rights. The same reference when nothing changes.
    /// <list type="bullet">
    /// <item><c>Edit</c> (the default) and <c>Read</c>: <c>Read</c>-denied attributes removed,
    /// <c>Edit</c>-denied ones read-only.</item>
    /// <item><c>New</c> (#264, G1/G2): <c>Read</c>- and <c>New</c>-denied attributes removed; an
    /// <c>Edit</c> deny does not apply, since a create is governed by <c>New</c>.</item>
    /// </list>
    /// ⚠️ Only ever narrows what a caller may <em>see</em>: every verb removes the <c>Read</c>-denied
    /// attributes. Whether a value may be <em>written</em> is still decided at save time.
    /// </summary>
    Task<EntityTypeDefinition> ForFormAsync(
        EntityTypeDefinition definition, string verb = SparkCoreActions.Edit, CancellationToken cancellationToken = default);

    /// <summary>
    /// Presents each object for the current caller and <paramref name="verb"/> (D13a), recursing into
    /// AsDetail rows (each judged by its own row type's rights), and marks it presented for the boundary
    /// net:
    /// <list type="bullet">
    /// <item><c>Read</c>, <c>Edit</c>: Read-denied removed, Edit-denied read-only.</item>
    /// <item><c>New</c>: Read-denied removed, New-denied read-only.</item>
    /// <item><c>Query</c>: Query-denied removed.</item>
    /// </list>
    /// Removed, not blanked, and remembered, so a write to a removed attribute is a no-op. Outside a
    /// request the caller is the system: nothing is removed, and nothing is marked.
    /// </summary>
    /// <remarks>
    /// The mapper's construction paths call this; an endpoint calls it only for an object that was
    /// not constructed for the caller — the posted object a save answers with, the effective object
    /// of a refresh.
    /// </remarks>
    Task PresentAsync(IEnumerable<PersistentObject> objects, string verb, CancellationToken cancellationToken = default);

    /// <summary>
    /// The caller's view of a query's metadata (<c>Queries/Get|List</c>, S7): sort columns, column
    /// overrides and the parent reference that name a <c>Query</c>-denied attribute of the row type
    /// are left out. The same reference when nothing changes.
    /// </summary>
    Task<SparkQuery> ForQueryMetadataAsync(SparkQuery query, CancellationToken cancellationToken = default);
}

[Register(typeof(IAttributeRightsEnforcement), ServiceLifetime.Scoped)]
internal sealed partial class AttributeRightsEnforcement : IAttributeRightsEnforcement
{
    [Inject] private readonly IAttributeRights attributeRights;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IQueryLoader queryLoader;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    private static readonly IReadOnlySet<string> None = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>AsDetail nests; a cycle of row types is stopped by the depth, as in the mapper.</summary>
    private const int MaxDepth = 8;

    private readonly Dictionary<(Guid Type, string Verb), IReadOnlySet<string>> denied = [];

    public async Task<IReadOnlySet<string>> GetDeniedAsync(
        EntityTypeDefinition definition, string verb, CancellationToken cancellationToken = default)
    {
        if (denied.TryGetValue((definition.Id, verb), out var cached))
            return cached;

        var rights = await attributeRights.GetEffectiveAsync(definition, verb, cancellationToken);
        var set = rights.IsUnrestricted
            ? None
            : rights.DeniedAttributes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> result = set.Count == 0 ? None : set;

        denied[(definition.Id, verb)] = result;
        return result;
    }

    public async Task<EntityTypeDefinition> ForQueryAsync(
        EntityTypeDefinition definition, CancellationToken cancellationToken = default)
    {
        var refused = await GetDeniedAsync(definition, SparkCoreActions.Query, cancellationToken);
        var unreadable = await GetDeniedAsync(definition, SparkCoreActions.Read, cancellationToken);
        if (refused.Count == 0 && unreadable.Count == 0)
            return definition;

        var copy = definition.ShallowCopy();
        copy.Attributes = [.. definition.Attributes
            .Where(a => !refused.Contains(a.Name))
            .Select(a => WithoutRefusedOptions(a, refused))];
        copy.Breadcrumb = WithoutRefusedTokens(definition.Breadcrumb, refused, unreadable);
        return copy;
    }

    public async Task<EntityTypeDefinition> ForFormAsync(
        EntityTypeDefinition definition, string verb = SparkCoreActions.Edit, CancellationToken cancellationToken = default)
    {
        var result = await PruneForFormAsync(definition, verb, cancellationToken);

        if (definition.DetailTypes is { Length: > 0 } detailTypes)
        {
            var pruned = new EntityTypeDefinition[detailTypes.Length];
            var changed = false;
            for (var i = 0; i < detailTypes.Length; i++)
            {
                pruned[i] = await PruneForFormAsync(detailTypes[i], verb, cancellationToken);
                changed |= !ReferenceEquals(pruned[i], detailTypes[i]);
            }

            if (changed)
            {
                if (ReferenceEquals(result, definition))
                    result = definition.ShallowCopy();
                result.DetailTypes = pruned;
            }
        }

        return result;
    }

    private async Task<EntityTypeDefinition> PruneForFormAsync(
        EntityTypeDefinition definition, string verb, CancellationToken cancellationToken)
    {
        var isNew = string.Equals(verb, SparkCoreActions.New, StringComparison.Ordinal);
        var unreadable = await GetDeniedAsync(definition, SparkCoreActions.Read, cancellationToken);
        var uncreatable = isNew ? await GetDeniedAsync(definition, SparkCoreActions.New, cancellationToken) : None;
        var uneditable = isNew ? None : await GetDeniedAsync(definition, SparkCoreActions.Edit, cancellationToken);
        var subQueries = await WithoutRefusedParentReferencesAsync(definition.Queries, cancellationToken);
        if (unreadable.Count == 0 && uncreatable.Count == 0 && uneditable.Count == 0 && ReferenceEquals(subQueries, definition.Queries))
            return definition;

        var attributes = new List<EntityAttributeDefinition>(definition.Attributes.Length);
        foreach (var declared in definition.Attributes)
        {
            if (unreadable.Contains(declared.Name) || uncreatable.Contains(declared.Name))
                continue;

            var attribute = WithoutRefusedOptions(WithoutRefusedOptions(declared, unreadable), uncreatable);
            if (uneditable.Contains(attribute.Name) && !attribute.IsReadOnly)
            {
                var readOnly = ReferenceEquals(attribute, declared) ? attribute.ShallowCopy() : attribute;
                readOnly.IsReadOnly = true;
                attributes.Add(readOnly);
            }
            else
            {
                attributes.Add(attribute);
            }
        }

        var copy = definition.ShallowCopy();
        copy.Attributes = [.. attributes];
        // The client renders the type's breadcrumb template itself (S7): a token naming a removed
        // attribute would say the attribute exists. The server renders a refused token empty, so
        // dropping it here gives the client the same output.
        copy.Breadcrumb = WithoutRefusedTokens(definition.Breadcrumb, unreadable, uncreatable);
        copy.Queries = subQueries;
        return copy;
    }

    public async Task<SparkQuery> ForQueryMetadataAsync(SparkQuery query, CancellationToken cancellationToken = default)
    {
        if (query.EntityType is null || modelLoader.GetEntityTypeByName(query.EntityType) is not { } rowType)
            return query;

        var refused = await GetDeniedAsync(rowType, SparkCoreActions.Query, cancellationToken);
        if (refused.Count == 0)
            return query;

        var sortColumns = query.SortColumns.Where(s => !refused.Contains(s.Property)).ToArray();
        var columns = query.Columns?.Where(c => !refused.Contains(c.Name)).ToArray();
        var parentReference = query.ParentReference is { } named && refused.Contains(named) ? null : query.ParentReference;
        if (sortColumns.Length == query.SortColumns.Length
            && (columns?.Length ?? 0) == (query.Columns?.Length ?? 0)
            && ReferenceEquals(parentReference, query.ParentReference))
        {
            return query;
        }

        // WithSortColumns copies; the flag it sets is [JsonIgnore] and this copy is only serialized.
        var copy = query.WithSortColumns(sortColumns);
        copy.Columns = columns;
        copy.ParentReference = parentReference;
        return copy;
    }

    /// <summary>
    /// A sub-query's <c>parentReference</c> names an attribute of the <em>query's</em> row type (S7); it
    /// is left out when the caller may not query that attribute. The same array when nothing changes.
    /// </summary>
    private async Task<SparkSubQuery[]> WithoutRefusedParentReferencesAsync(SparkSubQuery[] subQueries, CancellationToken cancellationToken)
    {
        SparkSubQuery[]? result = null;
        for (var i = 0; i < subQueries.Length; i++)
        {
            var entry = subQueries[i];
            if (string.IsNullOrEmpty(entry.ParentReference)
                || queryLoader.ResolveQuery(entry.Query) is not { EntityType: { } rowTypeName }
                || modelLoader.GetEntityTypeByName(rowTypeName) is not { } rowType
                || !(await GetDeniedAsync(rowType, SparkCoreActions.Query, cancellationToken)).Contains(entry.ParentReference))
            {
                continue;
            }

            result ??= [.. subQueries];
            result[i] = new SparkSubQuery { Query = entry.Query, SelectionMode = entry.SelectionMode };
        }
        return result ?? subQueries;
    }

    /// <summary>
    /// <paramref name="attribute"/> without the renderer options whose value names a refused attribute
    /// (<c>"titleAttribute": "Message"</c>, S7). The same reference when there is none.
    /// </summary>
    private static EntityAttributeDefinition WithoutRefusedOptions(EntityAttributeDefinition attribute, IReadOnlySet<string> refused)
    {
        if (refused.Count == 0 || attribute.RendererOptions is not { Count: > 0 } options
            || !options.Values.Any(v => NamesRefused(v, refused)))
        {
            return attribute;
        }

        var copy = attribute.ShallowCopy();
        copy.RendererOptions = WithoutRefused(options, refused);
        return copy;
    }

    /// <summary>The options left, or null when none is: an empty object would still be sent.</summary>
    private static Dictionary<string, object>? WithoutRefused(Dictionary<string, object> options, IReadOnlySet<string> refused)
    {
        var kept = options.Where(o => !NamesRefused(o.Value, refused)).ToDictionary(o => o.Key, o => o.Value);
        return kept.Count == 0 ? null : kept;
    }

    private static bool NamesRefused(object? value, IReadOnlySet<string> refused) => value switch
    {
        string name => refused.Contains(name),
        System.Text.Json.JsonElement { ValueKind: System.Text.Json.JsonValueKind.String } element => refused.Contains(element.GetString()!),
        _ => false,
    };

    /// <summary>
    /// <paramref name="template"/> without the <c>{tokens}</c> in either set, literals kept and
    /// re-escaped. The same string when no token is refused.
    /// </summary>
    internal static string? WithoutRefusedTokens(string? template, IReadOnlySet<string> refused, IReadOnlySet<string> alsoRefused)
    {
        if (string.IsNullOrEmpty(template) || (refused.Count == 0 && alsoRefused.Count == 0))
            return template;

        IReadOnlyList<Breadcrumb.BreadcrumbToken> tokens;
        try
        {
            tokens = Breadcrumb.BreadcrumbTemplate.Parse(template);
        }
        catch (FormatException)
        {
            // Model validation refuses a malformed template at startup; nothing here can render one.
            return null;
        }

        if (!tokens.OfType<Breadcrumb.FieldToken>().Any(t => refused.Contains(t.AttributeName) || alsoRefused.Contains(t.AttributeName)))
            return template;

        var sb = new System.Text.StringBuilder(template.Length);
        foreach (var token in tokens)
        {
            switch (token)
            {
                case Breadcrumb.LiteralToken literal:
                    sb.Append(literal.Text.Replace("{", "{{").Replace("}", "}}"));
                    break;
                case Breadcrumb.FieldToken field when !refused.Contains(field.AttributeName) && !alsoRefused.Contains(field.AttributeName):
                    sb.Append('{').Append(field.AttributeName).Append('}');
                    break;
            }
        }
        return sb.ToString();
    }

    public async Task PresentAsync(IEnumerable<PersistentObject> objects, string verb, CancellationToken cancellationToken = default)
    {
        // No HTTP caller (a background job, a message handler, a cron run) is the system, explicitly:
        // presenting for "nobody" would evaluate the anonymous role and strip what the job writes.
        // Nothing reaches a client from here, so there is nothing to mark either.
        if (httpContextAccessor?.HttpContext is not { } httpContext)
            return;

        var (removeVerb, readOnlyVerb) = Verbs(verb);
        var caller = SparkPresentation.CallerOf(httpContext);
        foreach (var po in objects)
            await PresentOneAsync(po, removeVerb, readOnlyVerb, caller, depth: 0, cancellationToken);
    }

    /// <summary>What a presentation verb removes and what it makes read-only.</summary>
    internal static (string Remove, string? ReadOnly) Verbs(string verb) => verb switch
    {
        SparkCoreActions.New => (SparkCoreActions.Read, SparkCoreActions.New),
        SparkCoreActions.Query => (SparkCoreActions.Query, null),
        SparkCoreActions.Read or SparkCoreActions.Edit => (SparkCoreActions.Read, SparkCoreActions.Edit),
        _ => throw new ArgumentException($"'{verb}' is not a presentation verb; use Read, Edit, New or Query.", nameof(verb)),
    };

    private async Task PresentOneAsync(
        PersistentObject po, string removeVerb, string? readOnlyVerb, object caller, int depth, CancellationToken cancellationToken)
    {
        if (depth > MaxDepth)
            return;

        po.PresentedFor = caller;
        if (modelLoader.GetEntityType(po.ObjectTypeId) is { } definition)
        {
            var refused = await GetDeniedAsync(definition, removeVerb, cancellationToken);
            if (refused.Count > 0)
            {
                po.PruneAttributes(refused);
                foreach (var attribute in po.Attributes)
                {
                    if (attribute.RendererOptions is { Count: > 0 } options && options.Values.Any(v => NamesRefused(v, refused)))
                        attribute.RendererOptions = WithoutRefused(options, refused);
                }
            }

            if (readOnlyVerb is not null)
            {
                var readOnly = await GetDeniedAsync(definition, readOnlyVerb, cancellationToken);
                if (readOnly.Count > 0)
                {
                    foreach (var attribute in po.Attributes)
                    {
                        if (readOnly.Contains(attribute.Name))
                            attribute.IsReadOnly = true;
                    }
                }
            }
        }

        foreach (var attribute in po.Attributes)
        {
            if (attribute is not PersistentObjectAttributeAsDetail asDetail)
                continue;

            if (asDetail.Object is not null)
                await PresentOneAsync(asDetail.Object, removeVerb, readOnlyVerb, caller, depth + 1, cancellationToken);
            foreach (var row in asDetail.Objects ?? [])
                await PresentOneAsync(row, removeVerb, readOnlyVerb, caller, depth + 1, cancellationToken);
        }
    }
}
