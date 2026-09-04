using MintPlayer.SourceGenerators.Attributes;

namespace MintPlayer.Spark.Messaging.Services;

/// <summary>
/// Access to the <see cref="IServiceCollection"/> after the container is built.
/// </summary>
/// <remarks>
/// Registered service descriptors are the only place the framework can learn which message types
/// have a recipient — a built <see cref="IServiceProvider"/> cannot be enumerated. Two things need
/// that list: the type allow-list that gates <c>Type.GetType</c>, and lane discovery, which starts a
/// pump for every lane that has a handler.
/// </remarks>
internal interface IServiceCollectionAccessor
{
    IServiceCollection Services { get; }
}

internal sealed partial class ServiceCollectionAccessor : IServiceCollectionAccessor
{
    [Inject] public IServiceCollection Services { get; }
}
