using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class AttributeDefinitionActions : DefaultPersistentObjectActions<EntityAttributeDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<EntityAttributeDefinition> GetAll() => fileService.LoadAllAttributes();

    public override Task<IEnumerable<EntityAttributeDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<EntityAttributeDefinition>>(fileService.LoadAllAttributes());

    public override Task<EntityAttributeDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllAttributes().FirstOrDefault(a => a.Id.ToString() == id));

    public override Task<EntityAttributeDefinition> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<EntityAttributeDefinition>(obj);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();

        var poName = entity.PersistentObjectName
            ?? throw new InvalidOperationException("PersistentObjectName is required to save an attribute");

        fileService.SaveAttribute(poName, entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        // Find which PO this attribute belongs to
        var attr = fileService.LoadAllAttributes().FirstOrDefault(a => a.Id.ToString() == id);
        if (attr?.PersistentObjectName != null)
        {
            fileService.DeleteAttribute(attr.PersistentObjectName, id);
        }
        return Task.CompletedTask;
    }
}
