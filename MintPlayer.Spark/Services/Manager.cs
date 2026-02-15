using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Services;

[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;

    public IRetryAccessor Retry => retry;

    public PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes)
    {
        return new PersistentObject
        {
            Id = null,
            Name = name,
            ObjectTypeId = Guid.Empty,
            Attributes = attributes,
        };
    }
}
