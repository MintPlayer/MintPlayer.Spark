using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitDefActions : DefaultPersistentObjectActions<ProgramUnitDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<ProgramUnitDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnitDef>>(fileService.LoadAllProgramUnits());

    public override Task<ProgramUnitDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnits().FirstOrDefault(u => u.Id == id));
}
