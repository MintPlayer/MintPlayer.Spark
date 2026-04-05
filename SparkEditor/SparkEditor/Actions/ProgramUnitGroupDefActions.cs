using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitGroupDefActions : DefaultPersistentObjectActions<ProgramUnitGroup>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<ProgramUnitGroup> GetAll() => fileService.LoadAllProgramUnitGroups();

    public override Task<IEnumerable<ProgramUnitGroup>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnitGroup>>(fileService.LoadAllProgramUnitGroups());

    public override Task<ProgramUnitGroup?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnitGroups().FirstOrDefault(g => g.Id.ToString() == id));

    public override Task<ProgramUnitGroup> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<ProgramUnitGroup>(obj);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        fileService.SaveProgramUnitGroup(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteProgramUnitGroup(id);
        return Task.CompletedTask;
    }
}
