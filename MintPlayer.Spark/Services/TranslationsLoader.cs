using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;

namespace MintPlayer.Spark.Services;

public interface ITranslationsLoader
{
    TranslatedString? Resolve(string key);
    IReadOnlyDictionary<string, TranslatedString> GetAll();
}

[Register(typeof(ITranslationsLoader), ServiceLifetime.Singleton)]
internal partial class TranslationsLoader : ITranslationsLoader
{
    public TranslatedString? Resolve(string key)
        => SparkTranslations.All.TryGetValue(key, out var ts) ? ts : null;

    public IReadOnlyDictionary<string, TranslatedString> GetAll()
        => SparkTranslations.All;
}
