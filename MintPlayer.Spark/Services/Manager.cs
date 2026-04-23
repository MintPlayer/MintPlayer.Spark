using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Services;

[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly IRequestCultureResolver requestCultureResolver;
    [Inject] private readonly IEntityMapper entityMapper;

    public IRetryAccessor Retry => retry;

    public PersistentObject NewPersistentObject(string name)
        => entityMapper.NewPersistentObject(name);

    public PersistentObject NewPersistentObject(Guid id)
        => entityMapper.NewPersistentObject(id);

    public PersistentObject NewPersistentObject<T>() where T : class
        => entityMapper.NewPersistentObject<T>();

    public string GetTranslatedMessage(string key, params object[] parameters)
    {
        var culture = requestCultureResolver.GetCurrentCulture();
        return GetMessage(key, culture, parameters);
    }

    public string GetMessage(string key, string language, params object[] parameters)
    {
        var translatedString = translationsLoader.Resolve(key);
        if (translatedString is null)
            return key;

        var template = translatedString.GetValue(language);
        if (string.IsNullOrEmpty(template))
            return key;

        return parameters.Length > 0
            ? string.Format(template, parameters)
            : template;
    }
}
