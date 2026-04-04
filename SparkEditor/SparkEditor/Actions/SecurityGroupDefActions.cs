using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityGroupDefActions : DefaultPersistentObjectActions<SecurityGroupDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<SecurityGroupDefinition> GetAll() => fileService.LoadAllSecurityGroups();

    public override Task<IEnumerable<SecurityGroupDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<SecurityGroupDefinition>>(fileService.LoadAllSecurityGroups());

    public override Task<SecurityGroupDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityGroups().FirstOrDefault(g => g.Id == id));

    public override Task<SecurityGroupDefinition> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<SecurityGroupDefinition>(obj);
        if (string.IsNullOrEmpty(entity.Id))
            entity.Id = $"SecurityGroupDefs/{Guid.NewGuid()}";
        fileService.SaveSecurityGroup(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteSecurityGroup(id);
        return Task.CompletedTask;
    }
}
