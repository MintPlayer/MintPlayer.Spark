using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class TranslationDefActions : DefaultPersistentObjectActions<TranslationEntry>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<TranslationEntry> GetAll() => fileService.LoadAllTranslations();

    public override Task<IEnumerable<TranslationEntry>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<TranslationEntry>>(fileService.LoadAllTranslations());

    public override Task<TranslationEntry?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllTranslations().FirstOrDefault(t => t.Id == id));

    public override Task<TranslationEntry> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<TranslationEntry>(obj);
        if (string.IsNullOrEmpty(entity.Id))
            entity.Id = $"TranslationDefs/{entity.Key}";
        fileService.SaveTranslation(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteTranslation(id);
        return Task.CompletedTask;
    }
}
