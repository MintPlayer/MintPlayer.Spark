using Microsoft.AspNetCore.Http;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Replication.Authentication;

namespace MintPlayer.Spark.Replication.Services;

/// <summary>
/// Whether a module still holds <c>Replicate/{contract}</c> (warehouse WX2: "the owner keeps a subscription only
/// while its grant holds"). Asked at subscription time inside the module's request, and again outside any request —
/// before every push and by the grant sweep — as the module's principal, in a scope of its own.
/// </summary>
internal interface IModuleGrants
{
    Task<bool> MayReplicateAsync(string module, string contract, CancellationToken cancellationToken = default);
}

internal sealed partial class ModuleGrants : IModuleGrants
{
    /// <summary>The verb of a replication grant; the target is the contract name.</summary>
    public const string ReplicateVerb = "Replicate";

    [Inject] private readonly IServiceScopeFactory scopeFactory;

    public async Task<bool> MayReplicateAsync(string module, string contract, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        var previous = accessor.HttpContext;

        // The permission pipeline judges the request's principal; there is no request here, so the module gets one,
        // carrying the same identity the endpoints establish.
        var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        context.EstablishModuleIdentity(module);
        accessor.HttpContext = context;
        try
        {
            return await scope.ServiceProvider.GetRequiredService<IPermissionService>()
                .IsAllowedAsync(ReplicateVerb, contract, cancellationToken);
        }
        finally
        {
            accessor.HttpContext = previous;
        }
    }
}
