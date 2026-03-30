using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class CustomActionDefActions : DefaultPersistentObjectActions<CustomActionDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<CustomActionDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<CustomActionDef>>(fileService.LoadAllCustomActions());

    public override Task<CustomActionDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllCustomActions().FirstOrDefault(a => a.Id == id));
}
