using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class LanguageDefActions : DefaultPersistentObjectActions<LanguageDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<LanguageDefinition> GetAll() => fileService.LoadAllLanguages();

    public override Task<IEnumerable<LanguageDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<LanguageDefinition>>(fileService.LoadAllLanguages());

    public override Task<LanguageDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllLanguages().FirstOrDefault(l => l.Id == id));
}
