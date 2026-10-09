using System.Security.Claims;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.IdentityProvider.Models;
using MintPlayer.Spark.IdentityProvider.Services;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.IdentityProvider.Interceptors;

/// <summary>
/// Validates an <see cref="OidcResource"/> on save (<c>docs/identity_provider_platform_PRD.md</c> D1).
/// <para>
/// <b>Scope names are unique by construction, not by a lookup.</b> The document id is natural,
/// <c>OidcResources/&lt;name&gt;</c>; an identity resource's name is its one scope and contains no dot;
/// an API resource's scopes must start with its own name and a dot. So <c>fleet.read</c> can only ever
/// live in <c>OidcResources/fleet</c>, and no identity scope can be spelled like an API scope. The
/// query-before-save this replaced raced two concurrent saves (O17).
/// </para>
/// </summary>
public sealed partial class OidcResourceInterceptors : IBeforeSave<OidcResource>, IAfterSave<OidcResource>
{
    [Inject] private readonly OidcAudit audit;

    public async ValueTask OnBeforeSaveAsync(OidcResource entity, SaveContext context)
    {
        entity.Name = entity.Name?.Trim() ?? string.Empty;
        ValidateName(entity.Name, nameof(entity.Name));

        if (entity.Kind is not (OidcResourceKinds.Identity or OidcResourceKinds.Api))
            throw new SparkValidationException(
                $"Kind must be '{OidcResourceKinds.Identity}' or '{OidcResourceKinds.Api}'.", nameof(entity.Kind));

        if (entity.Kind == OidcResourceKinds.Api)
            ValidateApiScopes(entity);
        else if (entity.Scopes.Count > 0)
            throw new SparkValidationException(
                "An identity resource is itself one scope; it has no scopes of its own.", nameof(entity.Scopes));

        var naturalId = OidcScopeCatalog.ResourceId(entity.Name);
        if (context.IsNew || string.IsNullOrEmpty(entity.Id))
        {
            // Not a session only when the interceptor is called by hand (unit tests of the rules above).
            if (context.Session is IAsyncDocumentSession session && await session.Advanced.ExistsAsync(naturalId))
                throw new SparkValidationException($"A resource named '{entity.Name}' already exists.", nameof(entity.Name));

            entity.Id = naturalId;
            if (entity.CreatedBy is null && context.User?.FindFirstValue(ClaimTypes.NameIdentifier) is { } userId)
            {
                entity.CreatedBy = userId;
                // The creator owns a new API until someone says otherwise (D4: owners approve
                // other applications' use of its scopes).
                if (entity.Kind == OidcResourceKinds.Api && entity.Owners.Count == 0)
                    entity.Owners.Add(userId);
            }
        }
        else if (!string.Equals(entity.Id, naturalId, StringComparison.OrdinalIgnoreCase))
        {
            throw new SparkValidationException(
                "A resource cannot be renamed: its name is its id, and for an API the audience every issued token carries. Create a new resource instead.",
                nameof(entity.Name));
        }
    }

    /// <summary>
    /// G26 (PRD D9): disabling a resource, or one scope of an API resource, revokes every valid token carrying
    /// the scopes that just went off. Grants stay (<see cref="OidcDisableCascade"/>).
    /// </summary>
    public async ValueTask OnAfterSaveAsync(OidcResource entity, SaveContext context)
    {
        if (context.Before is not OidcResource before || context.Session is not IAsyncDocumentSession session)
            return;

        var wasOn = EnabledScopes(before);
        var turnedOff = wasOn.Except(EnabledScopes(entity), StringComparer.Ordinal).ToList();
        if (turnedOff.Count == 0)
            return;

        var store = session.Advanced.DocumentStore;
        await OidcDisableCascade.RevokeScopesAsync(store, turnedOff);
        using var auditSession = store.OpenAsyncSession();
        await audit.RecordAsync(auditSession, OidcAuditKinds.ResourceChanged,
            context.User?.FindFirstValue(ClaimTypes.NameIdentifier),
            details: new Dictionary<string, string> { ["resource"] = entity.Name, ["disabledScopes"] = string.Join(' ', turnedOff) });
        await auditSession.SaveChangesAsync();
    }

    private static IEnumerable<string> EnabledScopes(OidcResource resource)
        => !resource.Enabled ? []
            : resource.Kind == OidcResourceKinds.Api ? resource.Scopes.Where(s => s.Enabled).Select(s => s.Name)
            : [resource.Name];

    private static void ValidateName(string name, string field)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new SparkValidationException("A name is required.", field);

        // Scope names travel space-delimited in the `scope` parameter and the `scope` claim, so a
        // name containing whitespace silently becomes two scopes, neither of which exists.
        if (name.Any(char.IsWhiteSpace))
            throw new SparkValidationException(
                $"'{name}' contains whitespace. Scopes are space-delimited on the wire, so it would be read as two.", field);

        // A dot is what makes an API scope's resource findable (OidcScopeCatalog); a resource name
        // with one would be ambiguous with the API scopes of the resource named by its prefix.
        if (name.Contains('.'))
            throw new SparkValidationException(
                $"'{name}' contains a dot. Dots separate an API's name from its scopes (fleet.read), so a resource name cannot contain one.", field);

        if (name.Contains('/'))
            throw new SparkValidationException($"'{name}' contains a slash, which a document id cannot carry.", field);
    }

    private static void ValidateApiScopes(OidcResource entity)
    {
        var prefix = entity.Name + ".";
        foreach (var scope in entity.Scopes)
        {
            scope.Name = scope.Name?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(scope.Name))
                throw new SparkValidationException("Every scope needs a name.", nameof(entity.Scopes));

            if (scope.Name.Any(char.IsWhiteSpace))
                throw new SparkValidationException(
                    $"'{scope.Name}' contains whitespace. Scopes are space-delimited on the wire, so it would be read as two.",
                    nameof(entity.Scopes));

            if (!scope.Name.StartsWith(prefix, StringComparison.Ordinal) || scope.Name.Length == prefix.Length)
                throw new SparkValidationException(
                    $"'{scope.Name}' must start with the API's name and a dot ('{prefix}…'), so every scope name is unique across APIs.",
                    nameof(entity.Scopes));
        }

        var duplicate = entity.Scopes.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Count() > 1);
        if (duplicate != null)
            throw new SparkValidationException($"Scope '{duplicate.Key}' is listed more than once.", nameof(entity.Scopes));
    }
}
