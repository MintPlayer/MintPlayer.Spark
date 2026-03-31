using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class TranslationDefActions : DefaultPersistentObjectActions<TranslationEntry>
{
    [Inject] private readonly ISparkEditorFileService fileService;

    public IEnumerable<TranslationEntry> GetAll() => fileService.LoadAllTranslations();

    public override Task<IEnumerable<TranslationEntry>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<TranslationEntry>>(fileService.LoadAllTranslations());

    public override Task<TranslationEntry?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllTranslations().FirstOrDefault(t => t.Id == id));
}
