using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Authorization.Models;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class SecurityRightDefActions : DefaultPersistentObjectActions<Right>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<Right> GetAll() => fileService.LoadAllSecurityRights();

    public override Task<IEnumerable<Right>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<Right>>(fileService.LoadAllSecurityRights());

    public override Task<Right?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllSecurityRights().FirstOrDefault(r => r.Id == id));

    public override Task<Right> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<Right>(obj);
        if (string.IsNullOrEmpty(entity.Id))
            entity.Id = $"SecurityRightDefs/{Guid.NewGuid()}";
        fileService.SaveSecurityRight(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteSecurityRight(id);
        return Task.CompletedTask;
    }
}
