using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class QueryDefinitionActions : DefaultPersistentObjectActions<SparkQuery>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<SparkQuery> GetAll() => fileService.LoadAllQueries();

    public override Task<IEnumerable<SparkQuery>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SparkQuery>>(fileService.LoadAllQueries());

    public override Task<SparkQuery?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllQueries().FirstOrDefault(q => q.Id.ToString() == id));

    public override Task<SparkQuery> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<SparkQuery>(obj);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();

        var poName = entity.PersistentObjectName
            ?? throw new InvalidOperationException("PersistentObjectName is required to save a query");

        fileService.SaveQuery(poName, entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        var query = fileService.LoadAllQueries().FirstOrDefault(q => q.Id.ToString() == id);
        if (query?.PersistentObjectName != null)
        {
            fileService.DeleteQuery(query.PersistentObjectName, id);
        }
        return Task.CompletedTask;
    }
}
