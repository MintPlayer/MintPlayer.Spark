using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

namespace SparkEditor.Actions;

public partial class LookupReferenceDefActions : DefaultPersistentObjectActions<LookupReferenceDef>
{
    [Inject] private readonly ILookupReferenceDiscoveryService discoveryService;

    public IEnumerable<LookupReferenceDef> GetAll()
        => discoveryService.GetAllLookupReferences().Select(info => new LookupReferenceDef
        {
            Id = info.Name,
            Name = info.Name,
            IsTransient = info.IsTransient,
            DisplayType = info.DisplayType,
        });

    public override Task<IEnumerable<LookupReferenceDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult(GetAll());

    public override Task<LookupReferenceDef?> OnLoadAsync(ISparkSession session, string id)
    {
        var info = discoveryService.GetLookupReference(id);
        if (info == null) return Task.FromResult<LookupReferenceDef?>(null);

        return Task.FromResult<LookupReferenceDef?>(new LookupReferenceDef
        {
            Id = info.Name,
            Name = info.Name,
            IsTransient = info.IsTransient,
            DisplayType = info.DisplayType,
        });
    }
}
