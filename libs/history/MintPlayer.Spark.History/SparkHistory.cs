using System.Globalization;
using System.Reflection;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Interceptors;
using MintPlayer.Spark.Abstractions.Reflection;
using MintPlayer.Spark.Services;
using Raven.Client.Documents;
using Raven.Client.Documents.Session;

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
    [Inject] private readonly IDocumentStore documentStore;
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

        using var session = documentStore.OpenAsyncSession();
        var metadata = await session.Advanced.Revisions.GetMetadataForAsync(id, skip, take, cancellationToken);
        var vectors = metadata.Select(m => m.TryGetValue("@change-vector", out var cv) ? cv?.ToString() : null).ToList();

        // One request for the page's contents, for who wrote each revision.
        var contents = typeof(IAuditModified).IsAssignableFrom(entityType)
            ? await LoadRevisionsAsync(session, entityType, vectors.Where(v => v is not null).Cast<string>().ToList())
            : new Dictionary<string, object?>();

        var userIds = contents.Values.OfType<IAuditModified>().Select(a => a.ModifiedBy).Where(u => !string.IsNullOrEmpty(u)).Cast<string>().Distinct().ToList();
        var names = userNames is not null && userIds.Count > 0
            ? await userNames.ResolveAsync(userIds, cancellationToken)
            : new Dictionary<string, string>();

        var result = new List<SparkRevision>(metadata.Count);
        for (var i = 0; i < metadata.Count; i++)
        {
            var changeVector = vectors[i];
            if (changeVector is null)
                continue;

            var userId = contents.TryGetValue(changeVector, out var content) ? (content as IAuditModified)?.ModifiedBy : null;
            var flags = metadata[i].TryGetValue("@flags", out var f) ? f?.ToString() ?? string.Empty : string.Empty;
            result.Add(new SparkRevision
            {
                ChangeVector = changeVector,
                LastModified = metadata[i].TryGetValue("@last-modified", out var lm) && DateTimeOffset.TryParse(lm?.ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                UserId = userId,
                UserName = userId is not null && names.TryGetValue(userId, out var name) ? name : null,
                IsDeleteRevision = flags.Contains("DeleteRevision", StringComparison.OrdinalIgnoreCase),
                IsCurrent = string.Equals(changeVector, current.Etag, StringComparison.Ordinal),
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

        var mapped = entityMapper.ToPersistentObject(revision, objectTypeId);
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
    /// is a revision of THIS document in THIS type's collection: a change vector is not scoped to a
    /// document, and RavenDB returns another document's revision for it (measured, spike H1).
    /// </summary>
    private async Task<object> LoadRevisionOfAsync(EntityTypeDefinition definition, Type entityType, string id, string changeVector)
    {
        if (string.IsNullOrEmpty(changeVector))
            throw new SparkRowLevelAccessDeniedException($"{HistoryRights.History}/{definition.Name}");

        using var session = documentStore.OpenAsyncSession();
        var loaded = await LoadRevisionsAsync(session, entityType, [changeVector]);
        if (!loaded.TryGetValue(changeVector, out var revision) || revision is null)
            throw new SparkRowLevelAccessDeniedException($"{HistoryRights.History}/{definition.Name}");

        var metadata = session.Advanced.GetMetadataFor(revision);
        var revisionId = metadata.TryGetValue("@id", out var rid) ? rid?.ToString() : null;
        var collection = metadata.TryGetValue("@collection", out var rc) ? rc?.ToString() : null;
        if (!string.Equals(revisionId, id, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(collection, documentStore.Conventions.FindCollectionName(entityType), StringComparison.OrdinalIgnoreCase))
            throw new SparkRowLevelAccessDeniedException($"{HistoryRights.History}/{definition.Name}");

        return revision;
    }

    private async Task<object?> LoadCurrentEntityAsync(Type entityType, string id)
    {
        using var session = documentStore.OpenAsyncSession();
        var load = LoadCurrentMethod.MakeGenericMethod(entityType);
        return await (Task<object?>)load.Invoke(null, [session, id])!;
    }

    private static Task<Dictionary<string, object?>> LoadRevisionsAsync(IAsyncDocumentSession session, Type entityType, IReadOnlyList<string> changeVectors)
    {
        if (changeVectors.Count == 0)
            return Task.FromResult(new Dictionary<string, object?>());
        var load = LoadRevisionsMethod.MakeGenericMethod(entityType);
        return (Task<Dictionary<string, object?>>)load.Invoke(null, [session, changeVectors])!;
    }

    private static readonly MethodInfo LoadRevisionsMethod =
        typeof(SparkHistory).GetMethod(nameof(LoadRevisionsTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly MethodInfo LoadCurrentMethod =
        typeof(SparkHistory).GetMethod(nameof(LoadCurrentTypedAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    // Typed, never <object>: an untyped load of a document without @Raven-Clr-Type is a JObject.
    private static async Task<Dictionary<string, object?>> LoadRevisionsTypedAsync<T>(IAsyncDocumentSession session, IReadOnlyList<string> changeVectors)
        where T : class
    {
        var loaded = await session.Advanced.Revisions.GetAsync<T>(changeVectors);
        return loaded.ToDictionary(pair => pair.Key, pair => (object?)pair.Value, StringComparer.Ordinal);
    }

    private static async Task<object?> LoadCurrentTypedAsync<T>(IAsyncDocumentSession session, string id) where T : class
        => await session.LoadAsync<T>(id);
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
