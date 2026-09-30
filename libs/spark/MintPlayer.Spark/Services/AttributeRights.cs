using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;

namespace MintPlayer.Spark.Services;

/// <summary>
/// The request's effective attribute rights (PRD §5 Q13), memoised per (type, verb).
/// </summary>
/// <remarks>
/// <b>Scoped, never process-wide</b>, for the same reason as <see cref="PermissionService"/>: the
/// decision depends on the caller. One evaluation per (type, verb) per request — enforcement asks
/// once and then applies the answer to every row and every attribute, so nothing here runs per row.
/// <para>
/// System context bypasses attribute rights (as it bypasses the shield today): sync and replication
/// act for the system, and a viewer-shaped attribute restriction would drop their writes.
/// </para>
/// </remarks>
[Register(typeof(IAttributeRights), ServiceLifetime.Scoped)]
internal partial class AttributeRights : IAttributeRights
{
    [Inject] private readonly IAccessControl accessControl;
    [Inject] private readonly IHttpContextAccessor? httpContextAccessor;

    private readonly Dictionary<(string Type, string Verb), EffectiveAttributeRights> decisions = new(KeyComparer.Instance);

    public Task<EffectiveAttributeRights> GetEffectiveAsync(
        EntityTypeDefinition entityType, string verb, CancellationToken cancellationToken = default)
        => GetEffectiveAsync(entityType.Name, entityType, verb, cancellationToken);

    public Task<EffectiveAttributeRights> GetEffectiveAsync(
        string entityTypeName, string verb, CancellationToken cancellationToken = default)
        => GetEffectiveAsync(entityTypeName, null, verb, cancellationToken);

    private async Task<EffectiveAttributeRights> GetEffectiveAsync(
        string entityTypeName, EntityTypeDefinition? definition, string verb, CancellationToken cancellationToken)
    {
        if (!SparkAttributeRights.IsVerb(verb))
        {
            throw new ArgumentException(
                $"'{verb}' has no attribute-level form; attribute rights exist for "
                + $"{string.Join(", ", SparkAttributeRights.Verbs)} only. Delete and custom actions stay type-level.",
                nameof(verb));
        }

        if (decisions.TryGetValue((entityTypeName, verb), out var cached))
            return cached;

        EffectiveAttributeRights result;
        if (Abstractions.Authentication.SparkSystemContext.IsSystemContext(httpContextAccessor))
        {
            result = EffectiveAttributeRights.Unrestricted(entityTypeName, verb);
        }
        else
        {
            result = Canonicalize(
                await accessControl.GetAttributeRightsAsync(verb, entityTypeName, cancellationToken), definition);
        }

        decisions[(entityTypeName, verb)] = result;
        return result;
    }

    /// <summary>
    /// The evaluator upper-cases names; give the attributes the model's spelling back, so a caller
    /// listing <see cref="EffectiveAttributeRights.DeniedAttributes"/> sees the names it would write.
    /// </summary>
    private static EffectiveAttributeRights Canonicalize(EffectiveAttributeRights rights, EntityTypeDefinition? definition)
    {
        if (definition is null || rights.Attributes.Count == 0)
            return rights;

        var canonical = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, allowed) in rights.Attributes)
        {
            var declared = definition.Attributes.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));
            canonical[declared?.Name ?? name] = allowed;
        }

        return new EffectiveAttributeRights(definition.Name, rights.Verb, rights.TypeAllowed, canonical, rights.IsUnrestricted);
    }

    private sealed class KeyComparer : IEqualityComparer<(string Type, string Verb)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Type, string Verb) x, (string Type, string Verb) y)
            => StringComparer.OrdinalIgnoreCase.Equals(x.Type, y.Type)
               && StringComparer.OrdinalIgnoreCase.Equals(x.Verb, y.Verb);

        public int GetHashCode((string Type, string Verb) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Type),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Verb));
    }
}
