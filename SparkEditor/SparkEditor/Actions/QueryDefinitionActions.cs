using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class QueryDefinitionActions : DefaultPersistentObjectActions<SparkQuery>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<SparkQuery> GetAll() => fileService.LoadAllQueries();

    public override Task<IEnumerable<SparkQuery>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SparkQuery>>(fileService.LoadAllQueries());

    public override Task<SparkQuery?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllQueries().FirstOrDefault(q => q.Id.ToString() == id));
}
