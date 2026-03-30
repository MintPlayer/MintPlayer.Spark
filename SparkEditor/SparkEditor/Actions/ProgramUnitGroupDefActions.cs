using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitGroupDefActions : DefaultPersistentObjectActions<ProgramUnitGroupDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<ProgramUnitGroupDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnitGroupDef>>(fileService.LoadAllProgramUnitGroups());

    public override Task<ProgramUnitGroupDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnitGroups().FirstOrDefault(g => g.Id == id));
}
