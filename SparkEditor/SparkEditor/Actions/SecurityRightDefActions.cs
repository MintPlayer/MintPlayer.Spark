using MintPlayer.Spark.Authorization.Models;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityRightDefActions : DefaultPersistentObjectActions<Right>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<Right> GetAll() => fileService.LoadAllSecurityRights();

    public override Task<IEnumerable<Right>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<Right>>(fileService.LoadAllSecurityRights());

    public override Task<Right?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityRights().FirstOrDefault(r => r.Id == id));
}
