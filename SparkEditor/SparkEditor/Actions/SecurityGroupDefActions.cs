using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityGroupDefActions : DefaultPersistentObjectActions<SecurityGroupDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<SecurityGroupDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SecurityGroupDef>>(fileService.LoadAllSecurityGroups());

    public override Task<SecurityGroupDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityGroups().FirstOrDefault(g => g.Id == id));
}
