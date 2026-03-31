using MintPlayer.Spark.Models;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class CustomActionDefActions : DefaultPersistentObjectActions<CustomActionDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<CustomActionDefinition> GetAll() => fileService.LoadAllCustomActions();

    public override Task<IEnumerable<CustomActionDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<CustomActionDefinition>>(fileService.LoadAllCustomActions());

    public override Task<CustomActionDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllCustomActions().FirstOrDefault(a => a.Id == id));
}
