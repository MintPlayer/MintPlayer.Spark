using MintPlayer.Spark.Abstractions.Sharing;

namespace MintPlayer.Spark.Sharing;

/// <summary><c>Spark:Share</c> (<c>docs/seo_analyzer_PRD.md</c> X6). Code given to <c>AddShareLinks</c> runs after the section.</summary>
public sealed class SparkShareOptions
{
    public const string SectionName = "Spark:Share";

    /// <summary>The global switch (D31). Off: no new link is made and every existing link reads as unknown.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The SPA page a link opens: <c>{PublicBaseUrl}{LinkPath}/{type}/{token}?id={id}</c>. Keep it in step with ng-spark's <c>withShareRoutes()</c>.</summary>
    public string LinkPath { get; set; } = "/shared";

    /// <summary>The origin of the links; null means <c>Spark:Auth:PublicBaseUrl</c>, else (the link is shown to its creator only) the request's origin.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Anonymous share reads per client address per minute (load and query together).</summary>
    public int RequestsPerMinute { get; set; } = 60;

    /// <summary>The largest page of a child query a share read returns.</summary>
    public int MaxTake { get; set; } = 100;

    internal Dictionary<string, SparkShareDefinition> Types { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Makes <typeparamref name="T"/> shareable: a link shows exactly <paramref name="attributes"/> of the object,
    /// and the child queries in <paramref name="queries"/> (query name → the child columns shown), each run with the
    /// shared object as its parent. Nothing else of the object or its children is ever sent.
    /// </summary>
    /// <param name="typeName">The model type name, when it differs from the CLR type's name.</param>
    public SparkShareOptions Share<T>(IEnumerable<string> attributes, IReadOnlyDictionary<string, string[]>? queries = null, string? typeName = null)
        where T : class, ISparkShareable
    {
        var name = typeName ?? typeof(T).Name;
        Types[name] = new SparkShareDefinition(name, typeof(T),
            new HashSet<string>(attributes, StringComparer.Ordinal),
            (queries ?? new Dictionary<string, string[]>()).ToDictionary(q => q.Key, q => (IReadOnlySet<string>)new HashSet<string>(q.Value, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase));
        return this;
    }
}

/// <summary>What a link to an object of <see cref="TypeName"/> shows.</summary>
internal sealed record SparkShareDefinition(
    string TypeName,
    Type ClrType,
    IReadOnlySet<string> Attributes,
    IReadOnlyDictionary<string, IReadOnlySet<string>> Queries);

/// <summary>
/// A gate an application adds to decide at runtime whether sharing is on (e.g. a settings switch, D31). Every
/// registered gate must say yes, on top of <see cref="SparkShareOptions.Enabled"/>.
/// </summary>
public interface ISparkShareGate
{
    ValueTask<bool> IsEnabledAsync(string typeName, CancellationToken cancellationToken);

    /// <summary>
    /// Whether one stored object may be shared now, asked after <see cref="IsEnabledAsync"/> said yes for its type: when
    /// a link is made and on every read of one. Decide from the stored <paramref name="entity"/> only (e.g. "only
    /// verified sites", SeoTester D11). Yes by default; a refusal reads like an unknown link (404), and a link the gate
    /// refuses works again once it says yes.
    /// </summary>
    ValueTask<bool> IsEnabledForAsync(string typeName, object entity, CancellationToken cancellationToken) => ValueTask.FromResult(true);
}
