using MintPlayer.Spark.Abstractions;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Actions;
using MintPlayer.Spark.Services;
using MintPlayer.Spark.Storage;

using SparkEditor.Services;

namespace SparkEditor.Actions;

public partial class LanguageDefActions : DefaultPersistentObjectActions<LanguageDefinition>
{
    [Inject] private readonly ISparkEditorFileService fileService;
    [Inject] private readonly IEntityMapper entityMapper;

    public IEnumerable<LanguageDefinition> GetAll() => fileService.LoadAllLanguages();

    public override Task<IEnumerable<LanguageDefinition>> OnQueryAsync(ISparkSession session)
        => Task.FromResult<IEnumerable<LanguageDefinition>>(fileService.LoadAllLanguages());

    public override Task<LanguageDefinition?> OnLoadAsync(ISparkSession session, string id)
        => Task.FromResult(fileService.LoadAllLanguages().FirstOrDefault(l => l.Id == id));

    public override Task<LanguageDefinition> OnSaveAsync(ISparkSession session, PersistentObject obj)
    {
        var entity = entityMapper.ToEntity<LanguageDefinition>(obj);
        if (string.IsNullOrEmpty(entity.Id))
            entity.Id = $"LanguageDefs/{entity.Culture}";
        fileService.SaveLanguage(entity);
        return Task.FromResult(entity);
    }

    public override Task OnDeleteAsync(ISparkSession session, string id)
    {
        fileService.DeleteLanguage(id);
        return Task.CompletedTask;
    }
}
