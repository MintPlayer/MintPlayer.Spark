using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitDefActions : DefaultPersistentObjectActions<ProgramUnit>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<ProgramUnit> GetAll() => fileService.LoadAllProgramUnits();

    public override Task<IEnumerable<ProgramUnit>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnit>>(fileService.LoadAllProgramUnits());

    public override Task<ProgramUnit?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnits().FirstOrDefault(u => u.Id.ToString() == id));

    public override Task<ProgramUnit> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<ProgramUnit>(obj);
        if (entity.Id == Guid.Empty) entity.Id = Guid.NewGuid();
        fileService.SaveProgramUnit(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteProgramUnit(id);
        return Task.CompletedTask;
    }
}
