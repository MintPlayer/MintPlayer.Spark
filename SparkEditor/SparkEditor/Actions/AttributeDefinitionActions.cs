using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class AttributeDefinitionActions : DefaultPersistentObjectActions<AttributeDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<AttributeDefinition> GetAll() => fileService.LoadAllAttributes();

    public override Task<IEnumerable<AttributeDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<AttributeDefinition>>(fileService.LoadAllAttributes());

    public override Task<AttributeDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllAttributes().FirstOrDefault(a => a.Id == id));
}
