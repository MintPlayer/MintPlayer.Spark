using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityRightDefActions : DefaultPersistentObjectActions<SecurityRightDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<SecurityRightDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SecurityRightDef>>(fileService.LoadAllSecurityRights());

    public override Task<SecurityRightDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityRights().FirstOrDefault(r => r.Id == id));
}
