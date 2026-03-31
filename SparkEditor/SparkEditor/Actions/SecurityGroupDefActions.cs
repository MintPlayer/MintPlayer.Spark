using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityGroupDefActions : DefaultPersistentObjectActions<SecurityGroupDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<SecurityGroupDefinition> GetAll() => fileService.LoadAllSecurityGroups();

    public override Task<IEnumerable<SecurityGroupDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SecurityGroupDefinition>>(fileService.LoadAllSecurityGroups());

    public override Task<SecurityGroupDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityGroups().FirstOrDefault(g => g.Id == id));
}
