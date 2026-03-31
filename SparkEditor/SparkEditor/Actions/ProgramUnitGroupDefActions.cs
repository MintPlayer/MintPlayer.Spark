using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitGroupDefActions : DefaultPersistentObjectActions<ProgramUnitGroup>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<ProgramUnitGroup> GetAll() => fileService.LoadAllProgramUnitGroups();

    public override Task<IEnumerable<ProgramUnitGroup>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnitGroup>>(fileService.LoadAllProgramUnitGroups());

    public override Task<ProgramUnitGroup?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnitGroups().FirstOrDefault(g => g.Id.ToString() == id));
}
