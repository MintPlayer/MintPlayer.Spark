using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class PersistentObjectDefinitionActions : DefaultPersistentObjectActions<EntityTypeDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<EntityTypeDefinition> GetAll() => fileService.LoadAllPersistentObjects();

    public override Task<IEnumerable<EntityTypeDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<EntityTypeDefinition>>(fileService.LoadAllPersistentObjects());

    public override Task<EntityTypeDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadPersistentObject(id));

    public override Task<EntityTypeDefinition> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<EntityTypeDefinition>(obj);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        fileService.SavePersistentObject(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeletePersistentObject(id);
        return Task.CompletedTask;
    }
}
