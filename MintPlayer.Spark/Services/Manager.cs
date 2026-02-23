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

    public IRetryAccessor Retry => retry;

    public PersistentObject NewPersistentObject(string name, params PersistentObjectAttribute[] attributes)
    {
        return new PersistentObject
        {
            Id = null,
            Name = name,
            ObjectTypeId = Guid.Empty,
            Attributes = attributes,
        };
    }

    public string GetTranslatedMessage(string key, params object[] parameters)
    {
        var culture = requestCultureResolver.GetCurrentCulture();
        return GetMessage(key, culture, parameters);
    }

    public string GetMessage(string key, string language, params object[] parameters)
    {
        var translations = translationsLoader.GetTranslations();
        if (!translations.TryGetValue(key, out var translatedString))
            return key;

        var template = translatedString.GetValue(language);
        if (string.IsNullOrEmpty(template))
            return key;

        return parameters.Length > 0
            ? string.Format(template, parameters)
            : template;
    }
}
