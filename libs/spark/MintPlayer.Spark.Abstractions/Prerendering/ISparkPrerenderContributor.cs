namespace MintPlayer.Spark.Abstractions.Prerendering;

/// <summary>
/// Predicts requests a server-rendered page is going to make, and supplies their responses
/// (docs/spark_ssr_PRD.md D3, D11). Every registered contributor runs for every server-rendered page,
/// so each one checks <see cref="ISparkPrerenderContext.Path"/> itself and returns at once for a page it
/// does not know.
/// <para>
/// A contributor never loads data itself: it asks the context to run the request the page will send,
/// and the context runs the endpoint in-process under the visitor's identity. What the page receives is
/// therefore exactly what the visitor would have fetched.
/// </para>
/// <para>
/// Register with <c>services.TryAddEnumerable(ServiceDescriptor.Scoped&lt;ISparkPrerenderContributor, T&gt;())</c>.
/// A misprediction costs nothing worse than a browser fetch after hydration (a "miss").
/// </para>
/// </summary>
public interface ISparkPrerenderContributor
{
    Task ContributeAsync(ISparkPrerenderContext context);
}

/// <summary>One server-rendered page, as the contributors see it.</summary>
public interface ISparkPrerenderContext
{
    /// <summary>
    /// The page request. ⚠️ Not thread-safe: contributors run concurrently, so read what you need from it
    /// before your first <c>await</c>, or use <see cref="Path"/>, <see cref="Language"/> and <see cref="TimeZone"/>.
    /// </summary>
    HttpContext HttpContext { get; }

    /// <summary>The page's path, without the path base, e.g. <c>/po/song/songs-1</c>.</summary>
    PathString Path { get; }

    /// <summary>The page's query string, e.g. <c>?deleted=only</c>, or empty.</summary>
    QueryString QueryString { get; }

    /// <summary>The language the page renders in (the <c>spark-lang</c> cookie, then <c>Accept-Language</c>, then the default).</summary>
    string Language { get; }

    /// <summary>The viewer's IANA zone (the timezone cookie), or the application's default zone.</summary>
    string TimeZone { get; }

    /// <summary>
    /// Runs <c>GET <paramref name="pathAndQuery"/></c> in-process and supplies its response to the page.
    /// Only an endpoint marked <see cref="SparkPrerenderableAttribute"/> runs; any other answers 404 and
    /// is not supplied. The same request asked twice runs once.
    /// </summary>
    /// <param name="pathAndQuery">Exactly the URL the page requests, e.g. <c>/spark/types</c>.</param>
    Task<SparkPrerenderResponse> GetAsync(string pathAndQuery);

    /// <summary>
    /// Runs <c>POST <paramref name="pathAndQuery"/></c> with a JSON body, as <see cref="GetAsync"/>.
    /// <paramref name="body"/> must hold the members the page sends (camelCase); a null member stands for
    /// one the page leaves out (<c>undefined</c>), and member order does not matter.
    /// </summary>
    Task<SparkPrerenderResponse> PostAsync(string pathAndQuery, object? body);

    /// <summary>
    /// The page's status: the status of its primary read (PRD D4), e.g. a 404 from the object the page
    /// shows. The first non-success status set wins.
    /// </summary>
    void SetPageStatus(int statusCode);

    /// <summary>Answers the page with a redirect instead of rendering it (e.g. to the canonical slug).</summary>
    void Redirect(string location, bool permanent);
}

/// <summary>A response the context ran for a page.</summary>
public sealed class SparkPrerenderResponse
{
    private static readonly System.Text.Json.JsonSerializerOptions ReadOptions = new(System.Text.Json.JsonSerializerDefaults.Web);

    public SparkPrerenderResponse(int status, string? body)
    {
        Status = status;
        Body = body;
    }

    public int Status { get; }

    /// <summary>The body exactly as the endpoint wrote it, or null when it wrote none.</summary>
    public string? Body { get; }

    public bool IsSuccess => Status is >= 200 and < 300;

    /// <summary>The body as <typeparamref name="T"/> (web defaults), or default when unsuccessful or empty.</summary>
    public T? Read<T>()
        => IsSuccess && !string.IsNullOrEmpty(Body) ? System.Text.Json.JsonSerializer.Deserialize<T>(Body, ReadOptions) : default;
}
