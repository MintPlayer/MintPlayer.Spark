using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class ProgramUnitDefActions : DefaultPersistentObjectActions<ProgramUnit>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<ProgramUnit> GetAll() => fileService.LoadAllProgramUnits();

    public override Task<IEnumerable<ProgramUnit>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<ProgramUnit>>(fileService.LoadAllProgramUnits());

    public override Task<ProgramUnit?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllProgramUnits().FirstOrDefault(u => u.Id.ToString() == id));
}
