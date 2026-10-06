using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using Raven.Client.Documents.Session;

namespace MintPlayer.Spark.Services;

/// <summary>
/// Turns an entity the caller has <b>already been authorized to see</b> into the persistent object a
/// load would return: breadcrumbs resolved, then field-level redaction (the Actions class's
/// <c>GetProtectedAttributesAsync</c>) applied for the caller. For add-on packages that render entity
/// content core did not load itself — History's old revisions (#460, M7).
/// </summary>
/// <remarks>
/// <para>
/// ⚠️ No row filter runs here: deciding that the caller may see this content is the caller's job
/// (History gates on the <b>current</b> document's row check). What this adds is the part an add-on
/// cannot get right alone — the same redaction every core read path applies, so a protected
/// attribute stays hidden on an old revision too (PRD risk 6).
/// </para>
/// <para>
/// <paramref name="alsoRedactAs"/> redacts additionally as if the row were that entity, and the
/// union is hidden: for a revision, pass the current document, so an attribute protected on
/// <em>either</em> state stays hidden.
/// </para>
/// </remarks>
public interface IPersistentObjectPresenter
{
    /// <summary>Maps, resolves breadcrumbs and redacts <paramref name="entity"/> as type <paramref name="objectTypeId"/>.</summary>
    Task<PersistentObject> PresentAsync(Guid objectTypeId, object entity, object? alsoRedactAs = null, CancellationToken cancellationToken = default);
}

[Register(typeof(IPersistentObjectPresenter), ServiceLifetime.Scoped)]
internal sealed partial class PersistentObjectPresenter : IPersistentObjectPresenter
{
    [Inject] private readonly IAsyncDocumentSession session;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IRowSecurity rowSecurity;
    [Inject] private readonly Breadcrumb.IBreadcrumbResolver breadcrumbResolver;

    public async Task<PersistentObject> PresentAsync(Guid objectTypeId, object entity, object? alsoRedactAs = null, CancellationToken cancellationToken = default)
    {
        var definition = modelLoader.GetEntityType(objectTypeId)
            ?? throw new InvalidOperationException($"Could not find EntityType with ID '{objectTypeId}'");
        var entityType = entity.GetType();

        var breadcrumbs = await breadcrumbResolver.ResolveAsync(session, [entity], definition, cancellationToken);
        // Built for the caller (D13a): a Read-denied attribute is absent from an old revision exactly as
        // from the current row (contributions M2c-2a).
        var po = await entityMapper.ToPersistentObjectAsync(entity, objectTypeId, breadcrumbs, cancellationToken: cancellationToken);

        // Redaction nulls attributes, so running it once per subject hides the union.
        await rowSecurity.RedactAsync(session, [(po, entity)], entityType, entityType, "Read", cancellationToken);
        if (alsoRedactAs is not null)
            await rowSecurity.RedactAsync(session, [(po, alsoRedactAs)], entityType, entityType, "Read", cancellationToken);

        return po;
    }
}
