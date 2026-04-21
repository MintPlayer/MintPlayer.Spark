using System.Net;

namespace MintPlayer.Spark.Client.Tests._Infrastructure;

/// <summary>
/// Test HttpMessageHandler that returns responses from a queue and records every request it
/// sees. Lets tests assert on exact URL, method, headers, and ordering without standing up a
/// whole TestServer — ideal for SparkClient edge-case tests (warmup failure shapes, query
/// parameter encoding, etc.).
/// </summary>
public sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = new();

    public ScriptedHttpHandler Enqueue(HttpResponseMessage response)
    {
        _responses.Enqueue(response);
        return this;
    }

    public ScriptedHttpHandler EnqueueOk() => Enqueue(new HttpResponseMessage(HttpStatusCode.OK));

    public ScriptedHttpHandler EnqueueStatus(HttpStatusCode status) => Enqueue(new HttpResponseMessage(status));

    /// <summary>Enqueue a 200 response that drops exactly the supplied Set-Cookie headers.</summary>
    public ScriptedHttpHandler EnqueueWithCookies(params string[] setCookies)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        foreach (var sc in setCookies)
            response.Headers.TryAddWithoutValidation("Set-Cookie", sc);
        return Enqueue(response);
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(CloneRequestForInspection(request));
        if (_responses.Count == 0)
            throw new InvalidOperationException($"No queued response for {request.Method} {request.RequestUri}.");
        return Task.FromResult(_responses.Dequeue());
    }

    // HttpRequestMessage is consumed after SendAsync returns (content stream drained);
    // snapshot the bits tests care about so they remain inspectable.
    private static HttpRequestMessage CloneRequestForInspection(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return clone;
    }
}
