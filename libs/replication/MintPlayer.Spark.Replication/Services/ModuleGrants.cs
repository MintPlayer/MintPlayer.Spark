using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Abstractions.Rendering;
using MintPlayer.Spark.Replication.Authentication;
using MintPlayer.Spark.Services;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>What a module may read of one source collection through one manifest.</summary>
/// <param name="Granted">It holds <c>Replicate/{SourceCollection}</c>.</param>
/// <param name="UnreadableFields">Manifest fields whose source attribute it may not read (F17, read side).</param>
internal sealed record ReplicaGrant(bool Granted, IReadOnlyList<string> UnreadableFields)
{
    public bool Allowed => Granted && UnreadableFields.Count == 0;

    /// <summary>Why it is not allowed, for the log and the consumer's answer.</summary>
    public string? Refusal => !Granted
        ? "Forbidden: no Replicate grant on the source collection"
        : UnreadableFields.Count > 0 ? $"Forbidden: may not read {string.Join(", ", UnreadableFields)}" : null;
}

/// <summary>
/// Whether a module may read a source collection through a manifest (warehouse §4.2.6): the
/// <c>Replicate/{SourceCollection}</c> grant (what the rights already say, <c>apps/*/App_Data/security.json</c>)
/// <b>and</b> the attribute read right of every source attribute the manifest reads. Asked at registration, at
/// approval, and again at every deploy and reconcile, as the module's principal in a scope of its own: a module is
/// evaluated like a person (F17).
/// </summary>
internal interface IModuleGrants
{
    Task<ReplicaGrant> CheckAsync(string module, string sourceCollection, ReplicaManifest manifest, CancellationToken cancellationToken = default);
}

internal sealed partial class ModuleGrants : IModuleGrants
{
    /// <summary>The verb of a replication grant; the target is the owner's source collection.</summary>
    public const string ReplicateVerb = "Replicate";

    [Inject] private readonly IServiceScopeFactory scopeFactory;
    [Inject] private readonly IReplicaSourceTypes sourceTypes;
    [Inject] private readonly IModelLoader modelLoader;

    public async Task<ReplicaGrant> CheckAsync(string module, string sourceCollection, ReplicaManifest manifest, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;
        // The permission pipeline judges the request's principal; outside a request the module gets one, carrying the
        // same identity the endpoints establish.
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.EstablishModuleIdentity(module);
        accessor.HttpContext = context;
        try
        {
            if (!await scope.ServiceProvider.GetRequiredService<IPermissionService>().IsAllowedAsync(ReplicateVerb, sourceCollection, cancellationToken))
                return new ReplicaGrant(false, []);

            // A collection the model does not govern has no attribute rights to check: Replicate alone was granted on
            // a name, and the register endpoint refuses such a collection before it gets here.
            if (sourceTypes.Resolve(sourceCollection) is not { } type
                || modelLoader.GetEntityTypeByClrType(type.FullName ?? type.Name) is not { } definition)
                return new ReplicaGrant(true, []);

            var rights = await scope.ServiceProvider.GetRequiredService<IAttributeRights>()
                .GetEffectiveAsync(definition, SparkCoreActions.Read, cancellationToken);
            var unreadable = manifest.Fields
                .Where(f => !rights.IsAllowed(f.Path[0]))
                .Select(f => f.Path[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new ReplicaGrant(true, unreadable);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }
}
