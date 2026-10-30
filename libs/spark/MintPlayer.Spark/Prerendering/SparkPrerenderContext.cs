using System.Collections.Concurrent;
using MintPlayer.Spark.Abstractions.Prerendering;

namespace MintPlayer.Spark.Prerendering;

/// <summary>
/// One server-rendered page: runs the requests the contributors predict, once per key, and collects
/// their responses for the render.
/// </summary>
internal sealed class SparkPrerenderContext : ISparkPrerenderContext
{
    private readonly SparkPrerenderDispatcher dispatcher;
    private readonly SparkPrerenderPage page;
    private readonly ConcurrentDictionary<string, Lazy<Task<(SparkPrerenderResponse Response, bool Refused)>>> requests = new();
    private int pageStatus;

    public SparkPrerenderContext(SparkPrerenderDispatcher dispatcher, HttpContext httpContext, string language, string timeZone)
    {
        this.dispatcher = dispatcher;
        HttpContext = httpContext;
        Path = httpContext.Request.Path;
        Language = language;
        TimeZone = timeZone;
        page = SparkPrerenderPage.From(httpContext, language, timeZone);
    }

    public HttpContext HttpContext { get; }
    public PathString Path { get; }
    public string Language { get; }
    public string TimeZone { get; }

    /// <summary>The status <see cref="SetPageStatus"/> settled on, or null for none.</summary>
    public int? PageStatus => pageStatus == 0 ? null : pageStatus;

    public (string Location, bool Permanent)? RedirectTo { get; private set; }

    /// <summary>What the page's requests cost so far.</summary>
    public SparkPrerenderCost Cost => page.Cost;

    public Task<SparkPrerenderResponse> GetAsync(string pathAndQuery) => SendAsync(HttpMethods.Get, pathAndQuery, "");

    public Task<SparkPrerenderResponse> PostAsync(string pathAndQuery, object? body)
        => SendAsync(HttpMethods.Post, pathAndQuery, SparkPrerenderRequestKey.CanonicalBody(body));

    private async Task<SparkPrerenderResponse> SendAsync(string method, string pathAndQuery, string canonicalBody)
    {
        var key = SparkPrerenderRequestKey.For(method, pathAndQuery, canonicalBody);
        var request = requests.GetOrAdd(key, _ => new Lazy<Task<(SparkPrerenderResponse, bool)>>(
            () => dispatcher.SendAsync(page, method, pathAndQuery, canonicalBody)));
        return (await request.Value).Response;
    }

    public void SetPageStatus(int statusCode)
    {
        if (statusCode is >= 200 and < 300)
            return;
        Interlocked.CompareExchange(ref pageStatus, statusCode, 0);
    }

    public void Redirect(string location, bool permanent) => RedirectTo ??= (location, permanent);

    /// <summary>
    /// Every response, once all requests finished: what the page receives (<c>data.spark.responses</c>).
    /// A refused request is left out, so the page misses it and the browser asks after hydration.
    /// </summary>
    public async Task<List<Dictionary<string, object?>>> ResponsesAsync()
    {
        var supplied = new List<Dictionary<string, object?>>();
        foreach (var (key, request) in requests)
        {
            var (response, refused) = await request.Value;
            if (refused)
                continue;
            supplied.Add(new Dictionary<string, object?>
            {
                ["key"] = key,
                ["status"] = response.Status,
                ["body"] = response.Body,
            });
        }
        return supplied;
    }
}
