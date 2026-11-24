namespace MintPlayer.Spark.Abstractions.Storage;

/// <summary>
/// The one place an <see cref="Id{T}"/> is made from outside the store (#536 D23 rule 3): a new id for an entity
/// about to be stored, or an id parsed from text a user, a URL or another system sent. Both go through the app
/// model, so the collection is checked, the key normalised and the whole id lowered (D7).
/// </summary>
public interface ISparkIds
{
    /// <summary>
    /// A new id for <typeparamref name="T"/>, known before the entity is stored, so references staged in the same
    /// session already carry it (D23 rule 5). Guid kind: a random v4 Guid. Integer kind: the provider's HiLo.
    /// </summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> has natural ids: its key comes from its own
    /// business key, not from here.</exception>
    Task<Id<T>> NewAsync<T>(CancellationToken cancellationToken = default) where T : class;

    /// <summary>
    /// Parses <paramref name="text"/> as an id of <typeparamref name="T"/>: either the whole id
    /// (<c>"people/42"</c>) or only its key (<c>"42"</c>).
    /// </summary>
    /// <exception cref="FormatException">The text is empty, or names another collection.</exception>
    Id<T> Parse<T>(string text) where T : class;

    /// <summary><see cref="Parse{T}"/> without the exception.</summary>
    bool TryParse<T>(string? text, out Id<T> id) where T : class;

    /// <summary>
    /// <see cref="Parse{T}"/> for a caller that holds a <see cref="Type"/>: the canonical string, or
    /// <see langword="null"/> when the text is not an id of <paramref name="entityType"/>.
    /// </summary>
    string? Canonicalize(Type entityType, string? text);

    /// <summary>How new ids of <paramref name="entityType"/> are made: the app model's choice, else the attribute.</summary>
    SparkIdKind KindOf(Type entityType);
}

/// <summary>
/// The app model's id choices (#536 D23 rule 2): an override of the <see cref="IdKindAttribute"/> default, also for
/// entities shipped in feature packages. Configure it through <c>services.Configure&lt;SparkIdOptions&gt;</c>.
/// </summary>
public sealed class SparkIdOptions
{
    /// <summary>The id kind per entity type, overriding its attribute.</summary>
    public Dictionary<Type, SparkIdKind> Kinds { get; } = [];
}

/// <summary>
/// A provider's integer id source (D23 rule 5): RavenDB's HiLo, EF's HiLo sequence. A provider that can't make
/// integer ids doesn't register one, and <see cref="ISparkIds.NewAsync{T}"/> says so for an Integer-kind entity.
/// </summary>
public interface ISparkIntegerIdSource
{
    /// <summary>The next key for <paramref name="entityType"/>, without the collection prefix (<c>"42"</c>, <c>"1-a"</c>).</summary>
    Task<string> NextKeyAsync(Type entityType, CancellationToken cancellationToken);
}
