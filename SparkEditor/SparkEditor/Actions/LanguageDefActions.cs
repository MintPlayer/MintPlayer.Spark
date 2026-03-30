using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class LanguageDefActions : DefaultPersistentObjectActions<LanguageDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<LanguageDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<LanguageDef>>(fileService.LoadAllLanguages());

    public override Task<LanguageDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllLanguages().FirstOrDefault(l => l.Id == id));
}
