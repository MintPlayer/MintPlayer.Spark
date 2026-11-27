using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Abstractions.Storage;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.History;

/// <inheritdoc />
internal sealed partial class SparkHistory : ISparkHistory
{
    /// <summary>Most revisions one list call returns.</summary>
    internal const int MaxTake = 200;

    [Inject] private readonly IDatabaseAccess databaseAccess;
    [Inject] private readonly IModelLoader modelLoader;
    [Inject] private readonly IPermissionService permissionService;
    [Inject] private readonly IEntityMapper entityMapper;
    [Inject] private readonly IPersistentObjectPresenter presenter;
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly ISparkRevisions revisions;
    [Inject] private readonly HistoryRequestState state;
    [Inject] private readonly IHistoryUserNameResolver? userNames;
    [Inject] private readonly IClientAccessor? clientAccessor;

    /// <summary>The warning a partial revert answers with (contributions M2c-2b).</summary>
    /// <summary>Every language travels; ng-spark shows the one its user picked (not the browser's).</summary>
    internal static readonly TranslatedString PartialRevertMessage = TranslatedString.Create(
        "Reverted partially: some attributes you may not edit kept their current values.",
        "Restauration partielle : certains attributs que vous ne pouvez pas modifier ont gardé leur valeur actuelle.",
        "Gedeeltelijk teruggezet: sommige attributen die u niet mag bewerken, behielden hun huidige waarde.");

    public async Task<IReadOnlyList<SparkRevision>> ListAsync(Guid objectTypeId, string id, int skip = 0, int take = 50, CancellationToken cancellationToken = default)
    {
        var (definition, entityType, current) = await GateAsync(objectTypeId, id, HistoryRights.History);
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxTake);

        var page = await revisions.ListAsync(entityType, id, skip, take, cancellationToken);

        // One request for the page's contents, for who wrote each revision.
        var contents = typeof(IAuditModified).IsAssignableFrom(entityType)
            ? await revisions.GetAsync(entityType, id, [.. page.Where(r => !r.IsDelete).Select(r => r.Version)], cancellationToken)
            : new Dictionary<string, object>();

        var userIds = contents.Values.OfType<IAuditModified>().Select(a => a.ModifiedBy).Where(u => !string.IsNullOrEmpty(u)).Cast<string>().Distinct().ToList();
        var names = userNames is not null && userIds.Count > 0
            ? await userNames.ResolveAsync(userIds, cancellationToken)
            : new Dictionary<string, string>();

        var result = new List<SparkRevision>(page.Count);
        foreach (var revision in page)
        {
            var userId = contents.TryGetValue(revision.Version, out var content) ? (content as IAuditModified)?.ModifiedBy : null;
            result.Add(new SparkRevision
            {
                ChangeVector = revision.Version,
                LastModified = revision.LastModified,
                UserId = userId,
                UserName = userId is not null && names.TryGetValue(userId, out var name) ? name : null,
                IsDeleteRevision = revision.IsDelete,
                IsCurrent = !revision.IsDelete && string.Equals(revision.Version, current.Etag, StringComparison.Ordinal),
            });
        }
        return result;
    }

    public async Task<PersistentObject> GetAsync(Guid objectTypeId, string id, string changeVector, CancellationToken cancellationToken = default)
    {
        var (definition, entityType, current) = await GateAsync(objectTypeId, id, HistoryRights.History);
        var revision = await LoadRevisionOfAsync(definition, entityType, id, changeVector);

        // Redacted as the revision AND as the current row: protected on either, hidden.
        var currentEntity = await LoadCurrentEntityAsync(entityType, id);
        var po = await presenter.PresentAsync(objectTypeId, revision, currentEntity, cancellationToken);
        po.Id = id;
        po.Etag = changeVector;
        po.Can = new PersistentObjectPermissions { Edit = false, Delete = false };
        return po;
    }

    public async Task<PersistentObject> RevertAsync(Guid objectTypeId, string id, string changeVector, CancellationToken cancellationToken = default)
    {
        var (definition, entityType, current) = await GateAsync(objectTypeId, id, HistoryRights.History);
        // Asked here as well as in IDatabaseAccess so that a caller without the right learns nothing
        // about the revision (not even whether the change vector is one of this row's).
        await permissionService.EnsureAuthorizedAsync(HistoryRights.Revert, definition.Name, cancellationToken);

        var revision = await LoadRevisionOfAsync(definition, entityType, id, changeVector);

        // A system construction (D13a): this is the save's input, not a response, and the write shield
        // judges each attribute by the caller's Edit right exactly as for a posted object.
        var mapped = entityMapper.AsSystem().ToPersistentObject(revision, objectTypeId);
        var satellites = entityType.GetSparkSatellitePropertyNames();
        var po = new PersistentObject
        {
            Id = id,
            Name = mapped.Name,
            ObjectTypeId = objectTypeId,
            // The row as it is NOW is what the revert replaces: a concurrent edit is a 409, not overwritten.
            Etag = current.Etag,
            // Audit fields are the stamping's, never the revision's. Satellite attributes
            // (contributions F2) were never stored, so a revision holds no value for them: posting
            // its empty value would withdraw every row an interceptor supplies. They are left as they are.
            Attributes = [.. mapped.Attributes.Where(a => !AuditFields.Contains(a.Name) && !satellites.Contains(a.Name))],
        };
        foreach (var attribute in po.Attributes)
            attribute.IsValueChanged = true;

        state.RevertSource = revision;
        state.RevertPartial = false;
        bool partial;
        try
        {
            await databaseAccess.SavePersistentObjectAsync(po, PersistentObjectOperation.Revert);
        }
        finally
        {
            state.RevertSource = null;
            partial = state.RevertPartial;
            state.RevertPartial = false;
        }

        // Attributes the caller may not edit keep their current values (contributions M2c-2b). The
        // save succeeded, so a silent partial revert would read as a complete one: say so, in the
        // envelope's operations, the channel every Spark client already shows.
        if (partial)
            clientAccessor?.Notify(PartialRevertMessage, NotificationKind.Warning);

        return await databaseAccess.GetPersistentObjectAsync(objectTypeId, id)
            ?? throw new SparkRowLevelAccessDeniedException($"{HistoryRights.Revert}/{definition.Name}");
    }

    private static readonly HashSet<string> AuditFields = new(StringComparer.Ordinal)
    {
        nameof(IAuditCreated.CreatedBy), nameof(IAuditCreated.CreatedAt), nameof(IAuditModified.ModifiedBy), nameof(IAuditModified.ModifiedAt),
    };

    /// <summary>
    /// The current row through the row-gated read (Read right + row gate), then <paramref name="right"/>.
    /// Every refusal is the same exception, so it answers like a missing row.
    /// </summary>
    private async Task<(EntityTypeDefinition Definition, Type EntityType, PersistentObject Current)> GateAsync(Guid objectTypeId, string id, string right)
    {
        var definition = modelLoader.GetEntityType(objectTypeId);
        var entityType = HistoryTypes.Resolve(definition?.ClrType);
        if (definition is null || entityType is null || string.IsNullOrEmpty(id))
            throw new SparkRowLevelAccessDeniedException($"{right}/{objectTypeId}");

        var current = await databaseAccess.GetPersistentObjectAsync(objectTypeId, id)
            ?? throw new SparkRowLevelAccessDeniedException($"{right}/{definition.Name}");

        await permissionService.EnsureAuthorizedAsync(right, definition.Name);
        return (definition, entityType, current);
    }

    /// <summary>
    /// The revision <paramref name="changeVector"/> as <paramref name="entityType"/>, refused unless it
    /// is a revision of THIS document in THIS type's collection (<see cref="ISparkRevisions.GetAsync"/> checks: a RavenDB
    /// change vector is not scoped to a document, spike H1).
    /// </summary>
    private async Task<object> LoadRevisionOfAsync(EntityTypeDefinition definition, Type entityType, string id, string changeVector)
    {
        if (string.IsNullOrEmpty(changeVector))
            throw new SparkRowLevelAccessDeniedException($"{HistoryRights.History}/{definition.Name}");

        var loaded = await revisions.GetAsync(entityType, id, [changeVector]);
        return loaded.TryGetValue(changeVector, out var revision)
            ? revision
            : throw new SparkRowLevelAccessDeniedException($"{HistoryRights.History}/{definition.Name}");
    }

    private async Task<object?> LoadCurrentEntityAsync(Type entityType, string id)
    {
        using var session = store.OpenSession();
        return await session.LoadAsync(entityType, id);
    }
}

/// <summary>Model CLR-type name to <see cref="Type"/>, the way core resolves it.</summary>
internal static class HistoryTypes
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Type?> Cache = new();

    public static Type? Resolve(string? clrType) => clrType is null ? null : Cache.GetOrAdd(clrType, static name =>
    {
        if (Type.GetType(name) is { } type)
            return type;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            if (assembly.GetType(name) is { } found)
                return found;
        return null;
    });
}
