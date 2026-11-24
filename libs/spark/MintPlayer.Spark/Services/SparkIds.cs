using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.Options;
using MintPlayer.SourceGenerators.Attributes;
using MintPlayer.Spark.Abstractions;
using MintPlayer.Spark.Abstractions.Storage;

namespace MintPlayer.Spark.Services;

/// <summary>
/// <see cref="ISparkIds"/> over the store's collection names (#536 D23 rule 3, D7). Stateless apart from caches
/// keyed by type, so several test hosts in one process never share a choice (D23 rule 2).
/// </summary>
/// <remarks>
/// Key normalisation is lenient on purpose: a Guid key is written in its <c>D</c> form, lower case (every id RavenDB
/// generated so far has that form, so parsing never moves a stored id), and an all-digit key loses its leading
/// zeros. A key that is neither is kept as it is, lowered: RavenDB's HiLo keys (<c>1-a</c>) and ids written
/// before a type chose its kind (O15: production ids are not re-keyed).
/// </remarks>
[Register(typeof(ISparkIds), ServiceLifetime.Singleton)]
internal partial class SparkIds : ISparkIds
{
    [Inject] private readonly ISparkStore store;
    [Inject] private readonly IOptions<SparkIdOptions> options;
    [Inject] private readonly IServiceProvider serviceProvider;

    private readonly ConcurrentDictionary<Type, string> prefixes = new();

    public async Task<Id<T>> NewAsync<T>(CancellationToken cancellationToken = default) where T : class
    {
        var key = KindOf(typeof(T)) switch
        {
            SparkIdKind.Guid => Guid.NewGuid().ToString("D"),
            SparkIdKind.Integer => await IntegerSource(typeof(T)).NextKeyAsync(typeof(T), cancellationToken),
            _ => throw new InvalidOperationException(
                $"{typeof(T).Name} has natural ids: build its id from its business key, not with NewAsync."),
        };
        return Id<T>.FromTrusted(Prefix(typeof(T)) + key);
    }

    public Id<T> Parse<T>(string text) where T : class
        => Canonicalize(typeof(T), text) is { } canonical
            ? Id<T>.FromTrusted(canonical)
            : throw new FormatException($"'{text}' is not an id of {typeof(T).Name}.");

    public bool TryParse<T>(string? text, out Id<T> id) where T : class
    {
        var canonical = Canonicalize(typeof(T), text);
        id = Id<T>.FromTrusted(canonical);
        return canonical is not null;
    }

    public string? Canonicalize(Type entityType, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var lowered = text.Trim().ToLowerInvariant();
        var prefix = Prefix(entityType);

        // Either the whole id or the key alone (D23 rule 4: a URL may carry only the key). Another collection's
        // prefix is not an id of this type: "companies/1" never parses as a person, even though "1" would.
        string key;
        if (lowered.StartsWith(prefix, StringComparison.Ordinal))
            key = lowered[prefix.Length..];
        else if (lowered.Contains('/') && KindOf(entityType) != SparkIdKind.Natural)
            return null;
        else
            key = lowered;

        if (key.Length == 0)
            return null;

        // A natural key is the business key as written (lowered): "042" may be a postcode, not a number.
        return prefix + (KindOf(entityType) == SparkIdKind.Natural ? key : NormalizeKey(key));
    }

    public SparkIdKind KindOf(Type entityType)
    {
        if (options.Value.Kinds.TryGetValue(entityType, out var chosen))
            return chosen;
        if (entityType.GetCustomAttribute<IdKindAttribute>(inherit: false) is { } attribute)
            return attribute.Kind;
        return typeof(IHasNaturalId).IsAssignableFrom(entityType) ? SparkIdKind.Natural : SparkIdKind.Guid;
    }

    private static string NormalizeKey(string key)
    {
        if (Guid.TryParse(key, out var guid))
            return guid.ToString("D");
        if (key.All(char.IsAsciiDigit))
        {
            var trimmed = key.TrimStart('0');
            return trimmed.Length == 0 ? "0" : trimmed;
        }
        return key;
    }

    private string Prefix(Type entityType)
        => prefixes.GetOrAdd(entityType, t => store.GetCollectionName(t).ToLowerInvariant() + "/");

    private ISparkIntegerIdSource IntegerSource(Type entityType)
        => serviceProvider.GetService(typeof(ISparkIntegerIdSource)) as ISparkIntegerIdSource
            ?? throw new InvalidOperationException(
                $"{entityType.Name} has integer ids, and the {store.ProviderName} provider can't make them.");
}
