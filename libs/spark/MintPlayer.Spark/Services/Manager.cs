using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Authorization;
using MintPlayer.Spark.Abstractions.ClientOperations;
using MintPlayer.Spark.Abstractions.Retry;

namespace MintPlayer.Spark.Services;

[Register(typeof(IManager), ServiceLifetime.Scoped)]
internal sealed partial class Manager : IManager
{
    [Inject] private readonly IRetryAccessor retry;
    [Inject] private readonly IClientAccessor client;
    [Inject] private readonly ITranslationsLoader translationsLoader;
    [Inject] private readonly IRequestCultureResolver requestCultureResolver;
    [Inject] private readonly IEntityMapper entityMapper;

    public IRetryAccessor Retry => retry;
    public IClientAccessor Client => client;

    // Only delegates: construction, and with it the caller's attribute rights, is the mapper's (S5).
    public Task<PersistentObject> GetPersistentObjectAsync(string name, string verb = SparkCoreActions.Read, CancellationToken cancellationToken = default)
        => entityMapper.GetPersistentObjectAsync(name, verb, cancellationToken);

    public Task<PersistentObject> GetPersistentObjectAsync(Guid id, string verb = SparkCoreActions.Read, CancellationToken cancellationToken = default)
        => entityMapper.GetPersistentObjectAsync(id, verb, cancellationToken);

    public Task<PersistentObject> GetPersistentObjectAsync<T>(string verb = SparkCoreActions.Read, CancellationToken cancellationToken = default) where T : class
        => entityMapper.GetPersistentObjectAsync<T>(verb, cancellationToken);

    public ISystemManager AsSystem() => entityMapper.AsSystem();

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
