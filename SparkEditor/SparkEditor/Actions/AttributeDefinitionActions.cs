using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class AttributeDefinitionActions : DefaultPersistentObjectActions<EntityAttributeDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<EntityAttributeDefinition> GetAll() => fileService.LoadAllAttributes();

    public override Task<IEnumerable<EntityAttributeDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<EntityAttributeDefinition>>(fileService.LoadAllAttributes());

    public override Task<EntityAttributeDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllAttributes().FirstOrDefault(a => a.Id.ToString() == id));
}
