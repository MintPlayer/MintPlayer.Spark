using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;
using SparkEditor.Entities;
using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class TranslationDefActions : DefaultPersistentObjectActions<TranslationDef>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public override Task<IEnumerable<TranslationDef>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<TranslationDef>>(fileService.LoadAllTranslations());

    public override Task<TranslationDef?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllTranslations().FirstOrDefault(t => t.Id == id));
}
