using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class PersistentObjectDefinitionActions : DefaultPersistentObjectActions<EntityTypeDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<EntityTypeDefinition> GetAll() => fileService.LoadAllPersistentObjects();

    public override Task<IEnumerable<EntityTypeDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<EntityTypeDefinition>>(fileService.LoadAllPersistentObjects());

    public override Task<EntityTypeDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadPersistentObject(id));
}
