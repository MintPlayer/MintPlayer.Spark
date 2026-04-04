using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Models;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class CustomActionDefActions : DefaultPersistentObjectActions<CustomActionDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<CustomActionDefinition> GetAll() => fileService.LoadAllCustomActions();

    public override Task<IEnumerable<CustomActionDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<CustomActionDefinition>>(fileService.LoadAllCustomActions());

    public override Task<CustomActionDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllCustomActions().FirstOrDefault(a => a.Id == id));

    public override Task<CustomActionDefinition> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<CustomActionDefinition>(obj);

        var name = entity.Name
            ?? throw new InvalidOperationException("Custom action Name is required");

        entity.Id = $"CustomActionDefs/{name}";
        fileService.SaveCustomAction(name, entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        var name = id.StartsWith("CustomActionDefs/") ? id["CustomActionDefs/".Length..] : id;
        fileService.DeleteCustomAction(name);
        return Task.CompletedTask;
    }
}
