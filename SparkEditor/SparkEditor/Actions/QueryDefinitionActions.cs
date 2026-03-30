using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class QueryDefinitionActions : DefaultPersistentObjectActions<QueryDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<QueryDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<QueryDefinition>>(fileService.LoadAllQueries());

    public override Task<QueryDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllQueries().FirstOrDefault(q => q.Id == id));
}
