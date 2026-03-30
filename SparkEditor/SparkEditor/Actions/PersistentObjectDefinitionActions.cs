using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class PersistentObjectDefinitionActions : DefaultPersistentObjectActions<PersistentObjectDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<PersistentObjectDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<PersistentObjectDefinition>>(fileService.LoadAllPersistentObjects());

    public override Task<PersistentObjectDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadPersistentObject(id));
}
